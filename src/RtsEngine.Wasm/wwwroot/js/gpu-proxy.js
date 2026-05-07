// WebGPU proxy — the Emscripten-equivalent translation layer for WebGPU.
// Maps integer handles to WebGPU objects, forwards calls 1:1.

(() => {
    let device = null;
    let context = null;
    let canvasFormat = null;
    let canvas = null;
    let depthTexture = null;
    let depthTextureSize = [0, 0];

    // Offscreen scene RT (Catlike-Coding water-tutorial port). Allocated
    // once at first use; the scene FBO mirrors the canvas so we render
    // the world into it and copyTextureToTexture-blit to the swap chain
    // at frame end. Water samples sceneDepth for the depth-difference
    // fog math and grabColor (snapshotted between terrain and water) for
    // the refraction background. Allocate-once because WebGPU bind groups
    // capture views at creation; resizing would orphan the water bind
    // group and we'd need a rebuild dance to support that.
    let sceneColorTex = null;     // RENDER_ATTACHMENT + TEXTURE_BINDING + COPY_SRC
    let sceneDepthTex = null;     // RENDER_ATTACHMENT + TEXTURE_BINDING (depth24plus)
    let grabColorTex  = null;     // TEXTURE_BINDING + COPY_DST (snapshot)
    let sceneSize = [0, 0];
    let inSceneFrame = false;
    let grabColorViewId = -1;     // stable handle in textureViews[]
    let sceneDepthViewId = -1;

    const shaderModules = [null];
    const pipelines = [null];
    const buffers = [null];
    const bindGroups = [null];
    const textures = [null];
    const textureViews = [null];
    const samplers = [null];
    const indexFormats = new Map(); // bufferId → 'uint16' | 'uint32'

    function register(table, obj) {
        const id = table.length;
        table.push(obj);
        return id;
    }

    function ensureDepthTexture() {
        if (!canvas || !device) return null;
        if (depthTexture && canvas.width === depthTextureSize[0] && canvas.height === depthTextureSize[1]) {
            return depthTexture;
        }
        if (depthTexture) depthTexture.destroy();
        depthTexture = device.createTexture({
            size: [canvas.width, canvas.height],
            format: 'depth24plus',
            usage: GPUTextureUsage.RENDER_ATTACHMENT,
        });
        depthTextureSize = [canvas.width, canvas.height];
        return depthTexture;
    }

    // Allocate the scene RT + grab once. We don't reallocate on canvas
    // resize because WebGPU bind groups capture views at creation time —
    // recreating the textures would orphan the water bind group. Canvas
    // resizes during a session render at the original size (cropped or
    // stretched at composite time); fixed-size canvases are unaffected.
    function ensureSceneTargets() {
        if (!canvas || !device) return;
        if (sceneColorTex) return;
        const w = canvas.width, h = canvas.height;
        if (w <= 0 || h <= 0) return;

        sceneColorTex = device.createTexture({
            size: [w, h],
            format: canvasFormat,
            usage: GPUTextureUsage.RENDER_ATTACHMENT
                 | GPUTextureUsage.TEXTURE_BINDING
                 | GPUTextureUsage.COPY_SRC,
        });
        grabColorTex = device.createTexture({
            size: [w, h],
            format: canvasFormat,
            usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST,
        });
        sceneDepthTex = device.createTexture({
            size: [w, h],
            format: 'depth24plus',
            usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.TEXTURE_BINDING,
        });
        sceneSize = [w, h];

        // Stable handles registered in the textureViews table so C# can
        // pass them to CreateBindGroup like any other texture binding.
        const grabView  = grabColorTex.createView();
        const depthView = sceneDepthTex.createView({ aspect: 'depth-only' });
        grabColorViewId  = textureViews.length; textureViews.push(grabView);
        sceneDepthViewId = textureViews.length; textureViews.push(depthView);
    }

    window.GPUProxy = {
        async init(canvasId) {
            window.GPUProxyInitError = '';
            if (!navigator.gpu) {
                console.error('WebGPU not supported in this browser');
                window.GPUProxyInitError = 'WebGPU not supported. Requires Chrome 121+/Safari 18+ on desktop or mobile.';
                return false;
            }
            try {
                const adapter = await navigator.gpu.requestAdapter();
                if (!adapter) {
                    window.GPUProxyInitError = 'No GPU adapter available.';
                    return false;
                }
                device = await adapter.requestDevice();
                device.lost.then(info => console.error('GPU device lost:', info));
                device.onuncapturederror = (e) => {
                    const msg = e.error?.message || e.error || 'unknown GPU error';
                    console.error('GPU error:', msg);
                    // Show on screen for mobile debugging
                    const el = document.getElementById('gpu-errors');
                    if (el) el.textContent = msg;
                };
                canvas = document.getElementById(canvasId);
                if (!canvas) {
                    window.GPUProxyInitError = `Canvas '${canvasId}' not found in document.`;
                    return false;
                }
                context = canvas.getContext('webgpu');
                if (!context) {
                    window.GPUProxyInitError = 'Failed to get WebGPU canvas context.';
                    return false;
                }
                canvasFormat = navigator.gpu.getPreferredCanvasFormat();
                // COPY_DST so endSceneFrame can copyTextureToTexture from
                // the offscreen scene RT into the swap-chain image. Set on
                // BOTH the init configure AND the resizeCanvas reconfigure
                // (the ResizeObserver fires immediately at startup).
                context.configure({
                    device,
                    format: canvasFormat,
                    alphaMode: 'premultiplied',
                    usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.COPY_DST,
                });
                return true;
            } catch (e) {
                window.GPUProxyInitError = 'WebGPU init failed: ' + (e?.message ?? e);
                console.error(e);
                return false;
            }
        },

        getInitError() { return window.GPUProxyInitError || ''; },

        getCanvasFormat() { return canvasFormat; },

        resizeCanvas() {
            if (!canvas || !context || !device) return;
            const dpr = window.devicePixelRatio || 1;
            const rect = canvas.getBoundingClientRect();
            canvas.width  = Math.floor(rect.width  * dpr);
            canvas.height = Math.floor(rect.height * dpr);
            context.configure({ device, format: canvasFormat, alphaMode: 'premultiplied' });
        },

        // ── Shader / Buffer / Pipeline ──────────────────────────

        createShaderModule(wgslCode) {
            const mod = device.createShaderModule({ code: wgslCode });
            // Log any compilation errors/warnings (async, won't block)
            mod.getCompilationInfo().then(info => {
                for (const msg of info.messages) {
                    const prefix = `[WGSL ${msg.type}] line ${msg.lineNum}: `;
                    if (msg.type === 'error') console.error(prefix + msg.message);
                    else if (msg.type === 'warning') console.warn(prefix + msg.message);
                }
            }).catch(() => {});
            return register(shaderModules, mod);
        },

        createVertexBuffer(floatData) {
            const f32 = new Float32Array(floatData);
            const buf = device.createBuffer({
                size: f32.byteLength,
                usage: GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST,
                mappedAtCreation: true,
            });
            new Float32Array(buf.getMappedRange()).set(f32);
            buf.unmap();
            return register(buffers, buf);
        },

        createIndexBuffer(ushortData) {
            const u16 = new Uint16Array(ushortData);
            const rawSize = u16.byteLength > 0 ? u16.byteLength : 4; // min 4 bytes
            const padded = rawSize % 4 === 0 ? rawSize : rawSize + 2;
            const buf = device.createBuffer({
                size: padded,
                usage: GPUBufferUsage.INDEX | GPUBufferUsage.COPY_DST,
                mappedAtCreation: true,
            });
            // getMappedRange's offset+size must be multiples of 4 — use the
            // padded size, then write only the source indices (the trailing
            // ushort, if any, is uninitialized but never indexed by a draw).
            new Uint16Array(buf.getMappedRange(0, padded)).set(u16);
            buf.unmap();
            const id = register(buffers, buf);
            indexFormats.set(id, 'uint16');
            return id;
        },

        createIndexBuffer32(uintData) {
            const u32 = new Uint32Array(uintData);
            const buf = device.createBuffer({
                size: u32.byteLength,
                usage: GPUBufferUsage.INDEX | GPUBufferUsage.COPY_DST,
                mappedAtCreation: true,
            });
            new Uint32Array(buf.getMappedRange()).set(u32);
            buf.unmap();
            const id = register(buffers, buf);
            indexFormats.set(id, 'uint32');
            return id;
        },

        createUniformBuffer(sizeBytes) {
            return register(buffers, device.createBuffer({
                size: sizeBytes,
                usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
            }));
        },

        writeBuffer(bufferId, floatData) {
            device.queue.writeBuffer(buffers[bufferId], 0, new Float32Array(floatData));
        },

        createRenderPipeline(shaderModuleId, vertexBufferLayouts) {
            const pipeline = device.createRenderPipeline({
                layout: 'auto',
                vertex: {
                    module: shaderModules[shaderModuleId],
                    entryPoint: 'vs_main',
                    buffers: vertexBufferLayouts.map(l => ({
                        arrayStride: l.arrayStride,
                        attributes: l.attributes.map(a => ({
                            format: a.format, offset: a.offset, shaderLocation: a.shaderLocation,
                        })),
                    })),
                },
                fragment: {
                    module: shaderModules[shaderModuleId],
                    entryPoint: 'fs_main',
                    targets: [{ format: canvasFormat }],
                },
                primitive: { topology: 'triangle-list', cullMode: 'back' },
                depthStencil: { format: 'depth24plus', depthWriteEnabled: true, depthCompare: 'less' },
            });
            return register(pipelines, pipeline);
        },

        createBindGroup(pipelineId, groupIndex, entries) {
            const pipeline = pipelines[pipelineId];
            const bg = device.createBindGroup({
                layout: pipeline.getBindGroupLayout(groupIndex),
                entries: entries.map(e => {
                    if (e.bufferId !== undefined && e.bufferId !== null) {
                        return { binding: e.binding, resource: { buffer: buffers[e.bufferId] } };
                    }
                    if (e.textureViewId !== undefined && e.textureViewId !== null) {
                        return { binding: e.binding, resource: textureViews[e.textureViewId] };
                    }
                    if (e.samplerId !== undefined && e.samplerId !== null) {
                        return { binding: e.binding, resource: samplers[e.samplerId] };
                    }
                    throw new Error('bind group entry missing bufferId/textureViewId/samplerId');
                }),
            });
            return register(bindGroups, bg);
        },

        // Pick the colour/depth views for this draw based on whether we
        // are inside BeginSceneFrame / EndSceneFrame. Inside the bracket,
        // every Render*() call lands in the offscreen scene RT; outside,
        // it goes to the swap chain (existing behaviour).
        // (Closure helpers — not part of the public GPUProxy surface.)

        render(pipelineId, vertexBufferId, indexBufferId, bindGroupId, indexCount) {
            if (!device || !context) return;
            const colorView = inSceneFrame
                ? sceneColorTex.createView()
                : context.getCurrentTexture().createView();
            const depthView = inSceneFrame
                ? sceneDepthTex.createView({ aspect: 'depth-only' })
                : ensureDepthTexture().createView();

            const encoder = device.createCommandEncoder();
            const pass = encoder.beginRenderPass({
                colorAttachments: [{
                    view: colorView,
                    clearValue: { r: 0.02, g: 0.02, b: 0.06, a: 1.0 },
                    loadOp: 'clear',
                    storeOp: 'store',
                }],
                depthStencilAttachment: {
                    view: depthView,
                    depthClearValue: 1.0,
                    depthLoadOp: 'clear',
                    depthStoreOp: 'store',
                },
            });

            pass.setPipeline(pipelines[pipelineId]);
            pass.setVertexBuffer(0, buffers[vertexBufferId]);
            pass.setIndexBuffer(buffers[indexBufferId], indexFormats.get(indexBufferId) || 'uint16');
            pass.setBindGroup(0, bindGroups[bindGroupId]);
            pass.drawIndexed(indexCount);
            pass.end();

            device.queue.submit([encoder.finish()]);
        },

        renderAdditional(pipelineId, vertexBufferId, indexBufferId, bindGroupId, indexCount) {
            if (!device || !context) return;
            const colorView = inSceneFrame
                ? sceneColorTex.createView()
                : context.getCurrentTexture().createView();
            const depthView = inSceneFrame
                ? sceneDepthTex.createView({ aspect: 'depth-only' })
                : ensureDepthTexture().createView();
            const encoder = device.createCommandEncoder();
            const pass = encoder.beginRenderPass({
                colorAttachments: [{
                    view: colorView,
                    loadOp: 'load',
                    storeOp: 'store',
                }],
                depthStencilAttachment: {
                    view: depthView,
                    depthLoadOp: 'load',
                    depthStoreOp: 'store',
                },
            });
            pass.setPipeline(pipelines[pipelineId]);
            pass.setVertexBuffer(0, buffers[vertexBufferId]);
            pass.setIndexBuffer(buffers[indexBufferId], indexFormats.get(indexBufferId) || 'uint16');
            pass.setBindGroup(0, bindGroups[bindGroupId]);
            pass.drawIndexed(indexCount);
            pass.end();
            device.queue.submit([encoder.finish()]);
        },

        // Variant of renderAdditional for the water pass: depth attached
        // read-only so the same depth texture can be sampled by the bound
        // pipeline (the water shader needs depth-test against terrain
        // depth AND a per-pixel depth read for the fog math). The
        // pipeline must have depth-write disabled — WebGPU validates
        // this against depthReadOnly:true.
        renderWaterPass(pipelineId, vertexBufferId, indexBufferId, bindGroupId, indexCount) {
            if (!device || !context || !inSceneFrame) return;
            const encoder = device.createCommandEncoder();
            const pass = encoder.beginRenderPass({
                colorAttachments: [{
                    view: sceneColorTex.createView(),
                    loadOp: 'load',
                    storeOp: 'store',
                }],
                depthStencilAttachment: {
                    view: sceneDepthTex.createView({ aspect: 'depth-only' }),
                    depthReadOnly: true,
                },
            });
            pass.setPipeline(pipelines[pipelineId]);
            pass.setVertexBuffer(0, buffers[vertexBufferId]);
            pass.setIndexBuffer(buffers[indexBufferId], indexFormats.get(indexBufferId) || 'uint16');
            pass.setBindGroup(0, bindGroups[bindGroupId]);
            pass.drawIndexed(indexCount);
            pass.end();
            device.queue.submit([encoder.finish()]);
        },

        createRenderPipelineAlphaBlend(shaderModuleId, vertexBufferLayouts) {
            const pipeline = device.createRenderPipeline({
                layout: 'auto',
                vertex: {
                    module: shaderModules[shaderModuleId],
                    entryPoint: 'vs_main',
                    buffers: vertexBufferLayouts.map(l => ({
                        arrayStride: l.arrayStride,
                        attributes: l.attributes.map(a => ({
                            format: a.format, offset: a.offset, shaderLocation: a.shaderLocation,
                        })),
                    })),
                },
                fragment: {
                    module: shaderModules[shaderModuleId],
                    entryPoint: 'fs_main',
                    targets: [{
                        format: canvasFormat,
                        blend: {
                            color: { srcFactor: 'src-alpha', dstFactor: 'one-minus-src-alpha', operation: 'add' },
                            alpha: { srcFactor: 'one', dstFactor: 'one-minus-src-alpha', operation: 'add' },
                        },
                    }],
                },
                primitive: { topology: 'triangle-list', cullMode: 'front' },
                depthStencil: { format: 'depth24plus', depthWriteEnabled: false, depthCompare: 'less' },
            });
            return register(pipelines, pipeline);
        },

        createRenderPipelineMarker(shaderModuleId, vertexBufferLayouts) {
            const pipeline = device.createRenderPipeline({
                layout: 'auto',
                vertex: {
                    module: shaderModules[shaderModuleId],
                    entryPoint: 'vs_main',
                    buffers: vertexBufferLayouts.map(l => ({
                        arrayStride: l.arrayStride,
                        attributes: l.attributes.map(a => ({
                            format: a.format, offset: a.offset, shaderLocation: a.shaderLocation,
                        })),
                    })),
                },
                fragment: {
                    module: shaderModules[shaderModuleId],
                    entryPoint: 'fs_main',
                    targets: [{
                        format: canvasFormat,
                        blend: {
                            color: { srcFactor: 'src-alpha', dstFactor: 'one-minus-src-alpha', operation: 'add' },
                            alpha: { srcFactor: 'one', dstFactor: 'one-minus-src-alpha', operation: 'add' },
                        },
                    }],
                },
                primitive: { topology: 'triangle-list', cullMode: 'none' },
                depthStencil: { format: 'depth24plus', depthWriteEnabled: false, depthCompare: 'less' },
            });
            return register(pipelines, pipeline);
        },

        createRenderPipelineLines(shaderModuleId, vertexBufferLayouts) {
            const pipeline = device.createRenderPipeline({
                layout: 'auto',
                vertex: {
                    module: shaderModules[shaderModuleId],
                    entryPoint: 'vs_main',
                    buffers: vertexBufferLayouts.map(l => ({
                        arrayStride: l.arrayStride,
                        attributes: l.attributes.map(a => ({
                            format: a.format, offset: a.offset, shaderLocation: a.shaderLocation,
                        })),
                    })),
                },
                fragment: {
                    module: shaderModules[shaderModuleId],
                    entryPoint: 'fs_main',
                    // Alpha-blended so callers can fade lines in/out via the
                    // shader's color uniform alpha. At alpha=1 the blend is a
                    // pass-through, so opaque uses (cell outline) are unchanged.
                    targets: [{
                        format: canvasFormat,
                        blend: {
                            color: { srcFactor: 'src-alpha', dstFactor: 'one-minus-src-alpha', operation: 'add' },
                            alpha: { srcFactor: 'one', dstFactor: 'one-minus-src-alpha', operation: 'add' },
                        },
                    }],
                },
                primitive: { topology: 'line-list' },
                depthStencil: { format: 'depth24plus', depthWriteEnabled: false, depthCompare: 'less-equal' },
            });
            return register(pipelines, pipeline);
        },

        // Opaque (no blend), depth-test, depth-write OFF, cull-back. The
        // water shader composites the refracted background through the fog
        // mix, so the output is opaque; depth-write off keeps the depth
        // attachment at terrain depth so other water fragments and post-
        // water passes still sort correctly against actual geometry.
        createRenderPipelineWater(shaderModuleId, vertexBufferLayouts) {
            const pipeline = device.createRenderPipeline({
                layout: 'auto',
                vertex: {
                    module: shaderModules[shaderModuleId],
                    entryPoint: 'vs_main',
                    buffers: vertexBufferLayouts.map(l => ({
                        arrayStride: l.arrayStride,
                        attributes: l.attributes.map(a => ({
                            format: a.format, offset: a.offset, shaderLocation: a.shaderLocation,
                        })),
                    })),
                },
                fragment: {
                    module: shaderModules[shaderModuleId],
                    entryPoint: 'fs_main',
                    targets: [{ format: canvasFormat }],
                },
                primitive: { topology: 'triangle-list', cullMode: 'back' },
                depthStencil: { format: 'depth24plus', depthWriteEnabled: false, depthCompare: 'less' },
            });
            return register(pipelines, pipeline);
        },

        // ── Offscreen scene-frame lifecycle ──────────────────────────────
        prepareSceneTargets() { ensureSceneTargets(); },
        getGrabColorView()    { return grabColorViewId;  },
        getSceneDepthView()   { return sceneDepthViewId; },

        beginSceneFrame() {
            ensureSceneTargets();
            inSceneFrame = true;
        },

        // Snapshot scene-color → grab so the water shader can sample a
        // stable refraction background captured before water draws.
        grabSceneColor() {
            if (!device || !inSceneFrame || !sceneColorTex || !grabColorTex) return;
            const encoder = device.createCommandEncoder();
            encoder.copyTextureToTexture(
                { texture: sceneColorTex },
                { texture: grabColorTex },
                [sceneSize[0], sceneSize[1], 1]
            );
            device.queue.submit([encoder.finish()]);
        },

        // Composite the offscreen scene RT → swap chain via
        // copyTextureToTexture (cheaper than a full-screen quad pass).
        // Subsequent Render*() calls go back to the swap chain so the
        // EngineUI / HUD pass renders on top of the composited frame.
        endSceneFrame() {
            if (!device || !context || !inSceneFrame || !sceneColorTex) {
                inSceneFrame = false;
                return;
            }
            const swapTex = context.getCurrentTexture();
            const encoder = device.createCommandEncoder();
            encoder.copyTextureToTexture(
                { texture: sceneColorTex },
                { texture: swapTex },
                [Math.min(sceneSize[0], swapTex.width), Math.min(sceneSize[1], swapTex.height), 1]
            );
            device.queue.submit([encoder.finish()]);
            inSceneFrame = false;
        },

        renderOverlay(pipelineId, vertexBufferId, indexBufferId, bindGroupId, indexCount) {
            if (!device || !context) return;
            const colorView = inSceneFrame
                ? sceneColorTex.createView()
                : context.getCurrentTexture().createView();
            const depthView = inSceneFrame
                ? sceneDepthTex.createView({ aspect: 'depth-only' })
                : ensureDepthTexture().createView();
            const encoder = device.createCommandEncoder();
            const pass = encoder.beginRenderPass({
                colorAttachments: [{
                    view: colorView,
                    loadOp: 'load',
                    storeOp: 'store',
                }],
                depthStencilAttachment: {
                    view: depthView,
                    depthClearValue: 1.0,
                    depthLoadOp: 'clear',
                    depthStoreOp: 'store',
                },
            });
            pass.setPipeline(pipelines[pipelineId]);
            pass.setVertexBuffer(0, buffers[vertexBufferId]);
            pass.setIndexBuffer(buffers[indexBufferId], indexFormats.get(indexBufferId) || 'uint16');
            pass.setBindGroup(0, bindGroups[bindGroupId]);
            pass.drawIndexed(indexCount);
            pass.end();
            device.queue.submit([encoder.finish()]);
        },

        renderNoBind(pipelineId, vertexBufferId, indexBufferId, indexCount) {
            if (!device || !context) return;
            const colorView = inSceneFrame
                ? sceneColorTex.createView()
                : context.getCurrentTexture().createView();
            const depthView = inSceneFrame
                ? sceneDepthTex.createView({ aspect: 'depth-only' })
                : ensureDepthTexture().createView();
            const encoder = device.createCommandEncoder();
            const pass = encoder.beginRenderPass({
                colorAttachments: [{
                    view: colorView,
                    loadOp: 'load',
                    storeOp: 'store',
                }],
                depthStencilAttachment: {
                    view: depthView,
                    depthLoadOp: 'load',
                    depthStoreOp: 'store',
                },
            });
            pass.setPipeline(pipelines[pipelineId]);
            pass.setVertexBuffer(0, buffers[vertexBufferId]);
            pass.setIndexBuffer(buffers[indexBufferId], indexFormats.get(indexBufferId) || 'uint16');
            pass.drawIndexed(indexCount);
            pass.end();
            device.queue.submit([encoder.finish()]);
        },

        destroyBuffer(id) {
            if (buffers[id]) { buffers[id].destroy(); buffers[id] = null; }
        },

        createRenderPipelineUI(shaderModuleId, vertexBufferLayouts) {
            const pipeline = device.createRenderPipeline({
                layout: 'auto',
                vertex: {
                    module: shaderModules[shaderModuleId],
                    entryPoint: 'vs_main',
                    buffers: vertexBufferLayouts.map(l => ({
                        arrayStride: l.arrayStride,
                        attributes: l.attributes.map(a => ({
                            format: a.format, offset: a.offset, shaderLocation: a.shaderLocation,
                        })),
                    })),
                },
                fragment: {
                    module: shaderModules[shaderModuleId],
                    entryPoint: 'fs_main',
                    targets: [{
                        format: canvasFormat,
                        blend: {
                            color: { srcFactor: 'src-alpha', dstFactor: 'one-minus-src-alpha', operation: 'add' },
                            alpha: { srcFactor: 'one', dstFactor: 'one-minus-src-alpha', operation: 'add' },
                        },
                    }],
                },
                primitive: { topology: 'triangle-list', cullMode: 'none' },
                depthStencil: { format: 'depth24plus', depthWriteEnabled: false, depthCompare: 'always' },
            });
            return register(pipelines, pipeline);
        },

        // ── Textures / Samplers ─────────────────────────────────

        async createTextureFromUrl(url) {
            try {
                console.log(`[GPU] Loading texture: ${url}`);
                const resp = await fetch(url);
                if (!resp.ok) throw new Error(`fetch ${url}: HTTP ${resp.status}`);
                const blob = await resp.blob();
                console.log(`[GPU] Fetched ${url}: ${blob.size} bytes, type=${blob.type}`);
                const bitmap = await createImageBitmap(blob);
                console.log(`[GPU] Bitmap: ${bitmap.width}×${bitmap.height}`);
                const tex = device.createTexture({
                    size: [bitmap.width, bitmap.height, 1],
                    format: 'rgba8unorm',
                    // RENDER_ATTACHMENT is required by Dawn's copyExternalImageToTexture
                    // validator (it's how the implementation actually writes the pixels).
                    usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST | GPUTextureUsage.RENDER_ATTACHMENT,
                });
                device.queue.copyExternalImageToTexture(
                    { source: bitmap },
                    { texture: tex },
                    [bitmap.width, bitmap.height, 1]
                );
                bitmap.close();
                const view = tex.createView();
                textures.push(tex);
                const id = register(textureViews, view);
                console.log(`[GPU] Texture loaded OK, id=${id}`);
                return id;
            } catch (e) {
                console.error(`[GPU] createTextureFromUrl(${url}) FAILED:`, e);
                // Return a 1x1 white fallback texture so rendering doesn't break
                const fallback = device.createTexture({
                    size: [1, 1, 1],
                    format: 'rgba8unorm',
                    usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST,
                });
                device.queue.writeTexture(
                    { texture: fallback },
                    new Uint8Array([255, 255, 255, 255]),
                    { bytesPerRow: 4 },
                    [1, 1, 1]
                );
                textures.push(fallback);
                return register(textureViews, fallback.createView());
            }
        },

        createSampler(filter, wrap) {
            const filt = filter === 'nearest' ? 'nearest' : 'linear';
            const addr = wrap === 'clamp' ? 'clamp-to-edge' : 'repeat';
            const sampler = device.createSampler({
                magFilter: filt,
                minFilter: filt,
                mipmapFilter: filt,
                addressModeU: addr,
                addressModeV: addr,
                addressModeW: addr,
            });
            return register(samplers, sampler);
        },
    };
})();
