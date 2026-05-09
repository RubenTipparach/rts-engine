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

    // ── Per-frame batching ──────────────────────────────────────────
    // The hot path used to be one createCommandEncoder + beginRenderPass +
    // queue.submit per draw call (and every Blazor → JS interop call carries
    // its own marshalling cost). With 20 terrain patches + water + atmosphere
    // + outline + UI + units + HP bars + path lines that's 40+ submits per
    // frame. queue.submit is a sync point — it tanks WebGPU performance.
    //
    // Instead, beginFrame opens ONE encoder for the whole frame; each render*
    // call appends a draw to the currently-open pass; we only end the pass +
    // start a new one when the load/clear ops actually change (Render clears
    // color+depth, RenderOverlay clears depth, RenderAdditional/RenderNoBind
    // load both). endFrame ends the open pass and submits once.
    let frameEncoder = null;
    let frameColorView = null;
    let frameDepthView = null;
    let openPass = null;
    let openColorOp = '';   // 'clear' | 'load'
    let openDepthOp = '';   // 'clear' | 'load'
    // 'swap' or 'scene'. Tracks which target the currently-open pass
    // attaches to so the scene-frame bracket forces a pass restart on
    // entry/exit (different colour + depth attachments).
    let openPassTarget = '';
    // True if the currently-open pass uses depthReadOnly:true (water pass).
    // That mode is incompatible with the regular pass attachment shape, so
    // a switch in either direction forces a new pass.
    let openPassDepthReadOnly = false;

    // Buffers read by draws in the currently-recorded (but not yet submitted)
    // command encoder. queue.writeBuffer is queued and executes before the
    // next submit, so writing a buffer that's already been bound by a
    // recorded draw would clobber the value that draw expected to read.
    // We detect that hazard and flush (submit + reopen the encoder) before
    // the offending write — see flushHazard() / writeBuffer().
    //
    // Hot example: RtsRenderer.DrawInstance reuses one _ubo across N units,
    // doing WriteBuffer + RenderAdditional per unit. Without this guard, all
    // N units would render with the LAST writeBuffer's contents.
    const frameReadBuffers = new Set();

    function flushHazardIfNeeded(bufferId) {
        if (!frameEncoder) return;
        if (!frameReadBuffers.has(bufferId)) return;
        if (openPass) { openPass.end(); openPass = null; }
        device.queue.submit([frameEncoder.finish()]);
        frameEncoder = device.createCommandEncoder();
        frameReadBuffers.clear();
        openColorOp = '';
        openDepthOp = '';
        openPassTarget = '';
        openPassDepthReadOnly = false;
    }

    function noteRead(bufferId) {
        if (bufferId) frameReadBuffers.add(bufferId);
    }

    function startPassIfNeeded(colorOp, depthOp) {
        if (!frameEncoder) {
            // No frame open — fall back to legacy single-draw pass so callers
            // outside the BeginFrame/EndFrame wrap still work. This path
            // creates and submits its own encoder.
            return null;
        }
        const target = inSceneFrame ? 'scene' : 'swap';
        // Reuse the open pass only if EVERY attachment-shaping flag matches
        // (load/clear ops, target, regular vs water-pass depth mode).
        if (openPass
            && openColorOp === colorOp
            && openDepthOp === depthOp
            && openPassTarget === target
            && !openPassDepthReadOnly) {
            return openPass;
        }
        if (openPass) { openPass.end(); openPass = null; }
        const colorAttachment = {
            view: viewForTarget('color', target),
            loadOp: colorOp === 'clear' ? 'clear' : 'load',
            storeOp: 'store',
        };
        if (colorOp === 'clear') colorAttachment.clearValue = { r: 0.02, g: 0.02, b: 0.06, a: 1.0 };
        const depthAttachment = {
            view: viewForTarget('depth', target),
            depthLoadOp: depthOp === 'clear' ? 'clear' : 'load',
            depthStoreOp: 'store',
        };
        if (depthOp === 'clear') depthAttachment.depthClearValue = 1.0;
        openPass = frameEncoder.beginRenderPass({
            colorAttachments: [colorAttachment],
            depthStencilAttachment: depthAttachment,
        });
        openColorOp = colorOp;
        openDepthOp = depthOp;
        openPassTarget = target;
        openPassDepthReadOnly = false;
        return openPass;
    }

    // Variant for the water draw: scene depth attached read-only so the
    // pipeline can also sample it from the bind group while the pass
    // depth-tests against it. Always targets the scene RT (water only
    // makes sense inside a scene frame).
    function startWaterPassIfNeeded() {
        if (!frameEncoder || !inSceneFrame) return null;
        if (openPass && openPassDepthReadOnly && openPassTarget === 'scene') {
            return openPass;
        }
        if (openPass) { openPass.end(); openPass = null; }
        openPass = frameEncoder.beginRenderPass({
            colorAttachments: [{
                view: viewForTarget('color', 'scene'),
                loadOp: 'load',
                storeOp: 'store',
            }],
            depthStencilAttachment: {
                view: viewForTarget('depth', 'scene'),
                depthReadOnly: true,
            },
        });
        openColorOp = 'load';
        openDepthOp = '';
        openPassTarget = 'scene';
        openPassDepthReadOnly = true;
        return openPass;
    }

    // Resolve the colour or depth view for a given target. Cached at the
    // start of the frame for the swap chain (getCurrentTexture isn't stable
    // across calls within a frame); the scene RT views can be created on
    // the fly cheaply since the underlying textures are stable.
    function viewForTarget(kind, target) {
        if (target === 'scene') {
            return kind === 'color'
                ? sceneColorTex.createView()
                : sceneDepthTex.createView({ aspect: 'depth-only' });
        }
        return kind === 'color' ? frameColorView : frameDepthView;
    }

    // Force-close the open pass (target/mode about to change). Cheap when
    // there's nothing open.
    function endOpenPass() {
        if (openPass) { openPass.end(); openPass = null; }
        openColorOp = '';
        openDepthOp = '';
        openPassDepthReadOnly = false;
    }

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
            // Must keep COPY_DST in the swap-chain usage flags every time
            // we re-configure: endSceneFrame uses copyTextureToTexture from
            // the offscreen scene RT into the swap-chain image. Without
            // this flag, the copy is rejected and every frame after the
            // initial resize-observer fire errors. Same flags as init().
            context.configure({
                device,
                format: canvasFormat,
                alphaMode: 'premultiplied',
                usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.COPY_DST,
            });
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
            // If a draw in the currently-open encoder already binds this
            // buffer, flushing here keeps the frame correct. The flushed
            // submit becomes one of (typically) very few mid-frame submits
            // — only the RTS per-unit UBO pattern triggers it.
            flushHazardIfNeeded(bufferId);
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
            // Stash the buffer ids referenced by this bind group so render*()
            // can register them with frameReadBuffers (drives the
            // writeBuffer-after-draw hazard detector).
            bg.__bufferIds = [];
            for (const e of entries) {
                if (e.bufferId !== undefined && e.bufferId !== null) bg.__bufferIds.push(e.bufferId);
            }
            return register(bindGroups, bg);
        },

        beginFrame() {
            if (!device || !context) return;
            // Resolve the swapchain + depth view ONCE per frame —
            // getCurrentTexture is not stable across multiple calls in a frame
            // and creating a fresh view per draw was extra GC churn anyway.
            // Scene-RT views are resolved on the fly (cheap; underlying
            // textures are stable).
            frameColorView = context.getCurrentTexture().createView();
            const depthTex = ensureDepthTexture();
            frameDepthView = depthTex ? depthTex.createView() : null;
            frameEncoder = device.createCommandEncoder();
            openPass = null;
            openColorOp = '';
            openDepthOp = '';
            openPassTarget = '';
            openPassDepthReadOnly = false;
            frameReadBuffers.clear();
        },

        endFrame() {
            if (!frameEncoder) return;
            if (openPass) { openPass.end(); openPass = null; }
            device.queue.submit([frameEncoder.finish()]);
            frameEncoder = null;
            frameColorView = null;
            frameDepthView = null;
            openPassTarget = '';
            openPassDepthReadOnly = false;
            frameReadBuffers.clear();
        },

        render(pipelineId, vertexBufferId, indexBufferId, bindGroupId, indexCount) {
            if (!device || !context) return;
            const pass = startPassIfNeeded('clear', 'clear');
            if (pass) {
                const bg = bindGroups[bindGroupId];
                pass.setPipeline(pipelines[pipelineId]);
                pass.setVertexBuffer(0, buffers[vertexBufferId]);
                pass.setIndexBuffer(buffers[indexBufferId], indexFormats.get(indexBufferId) || 'uint16');
                pass.setBindGroup(0, bg);
                pass.drawIndexed(indexCount);
                noteRead(vertexBufferId); noteRead(indexBufferId);
                if (bg && bg.__bufferIds) for (const id of bg.__bufferIds) noteRead(id);
                return;
            }
            // Legacy unbatched path — only hit if a caller forgets BeginFrame.
            // Picks scene-RT views when inSceneFrame so the fallback still
            // works during the offscreen pass; outside the bracket it
            // targets the swap chain.
            const colorView = inSceneFrame
                ? sceneColorTex.createView()
                : context.getCurrentTexture().createView();
            const depthView = inSceneFrame
                ? sceneDepthTex.createView({ aspect: 'depth-only' })
                : ensureDepthTexture().createView();
            const encoder = device.createCommandEncoder();
            const p = encoder.beginRenderPass({
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
            p.setPipeline(pipelines[pipelineId]);
            p.setVertexBuffer(0, buffers[vertexBufferId]);
            p.setIndexBuffer(buffers[indexBufferId], indexFormats.get(indexBufferId) || 'uint16');
            p.setBindGroup(0, bindGroups[bindGroupId]);
            p.drawIndexed(indexCount);
            p.end();
            device.queue.submit([encoder.finish()]);
        },

        renderAdditional(pipelineId, vertexBufferId, indexBufferId, bindGroupId, indexCount) {
            if (!device || !context) return;
            const pass = startPassIfNeeded('load', 'load');
            if (pass) {
                const bg = bindGroups[bindGroupId];
                pass.setPipeline(pipelines[pipelineId]);
                pass.setVertexBuffer(0, buffers[vertexBufferId]);
                pass.setIndexBuffer(buffers[indexBufferId], indexFormats.get(indexBufferId) || 'uint16');
                pass.setBindGroup(0, bg);
                pass.drawIndexed(indexCount);
                noteRead(vertexBufferId); noteRead(indexBufferId);
                if (bg && bg.__bufferIds) for (const id of bg.__bufferIds) noteRead(id);
                return;
            }
            // Legacy fallback when BeginFrame wasn't called.
            const colorView = inSceneFrame
                ? sceneColorTex.createView()
                : context.getCurrentTexture().createView();
            const depthView = inSceneFrame
                ? sceneDepthTex.createView({ aspect: 'depth-only' })
                : ensureDepthTexture().createView();
            const encoder = device.createCommandEncoder();
            const p = encoder.beginRenderPass({
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
            p.setPipeline(pipelines[pipelineId]);
            p.setVertexBuffer(0, buffers[vertexBufferId]);
            p.setIndexBuffer(buffers[indexBufferId], indexFormats.get(indexBufferId) || 'uint16');
            p.setBindGroup(0, bindGroups[bindGroupId]);
            p.drawIndexed(indexCount);
            p.end();
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
            const pass = startWaterPassIfNeeded();
            if (pass) {
                const bg = bindGroups[bindGroupId];
                pass.setPipeline(pipelines[pipelineId]);
                pass.setVertexBuffer(0, buffers[vertexBufferId]);
                pass.setIndexBuffer(buffers[indexBufferId], indexFormats.get(indexBufferId) || 'uint16');
                pass.setBindGroup(0, bg);
                pass.drawIndexed(indexCount);
                noteRead(vertexBufferId); noteRead(indexBufferId);
                if (bg && bg.__bufferIds) for (const id of bg.__bufferIds) noteRead(id);
                return;
            }
            // Legacy fallback (no BeginFrame in flight) — submit a one-off
            // encoder so the water draw still works in unbatched mode.
            const encoder = device.createCommandEncoder();
            const p = encoder.beginRenderPass({
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
            p.setPipeline(pipelines[pipelineId]);
            p.setVertexBuffer(0, buffers[vertexBufferId]);
            p.setIndexBuffer(buffers[indexBufferId], indexFormats.get(indexBufferId) || 'uint16');
            p.setBindGroup(0, bindGroups[bindGroupId]);
            p.drawIndexed(indexCount);
            p.end();
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
            // The next render*() will see inSceneFrame=true and (via
            // startPassIfNeeded's target check) end the swap-chain pass
            // and start a new one against the scene RT. Force-end now so
            // the very first scene-RT draw doesn't try to re-use a swap
            // chain pass.
            endOpenPass();
        },

        // Snapshot scene-color → grab so the water shader can sample a
        // stable refraction background captured before water draws.
        // Encoded into the shared frame encoder so it composes with
        // beginFrame/endFrame batching (one queue.submit per frame).
        grabSceneColor() {
            if (!device || !inSceneFrame || !sceneColorTex || !grabColorTex) return;
            // Copy must be outside any open pass.
            endOpenPass();
            if (frameEncoder) {
                frameEncoder.copyTextureToTexture(
                    { texture: sceneColorTex },
                    { texture: grabColorTex },
                    [sceneSize[0], sceneSize[1], 1]
                );
            } else {
                // Legacy path — no frame open, submit our own encoder.
                const encoder = device.createCommandEncoder();
                encoder.copyTextureToTexture(
                    { texture: sceneColorTex },
                    { texture: grabColorTex },
                    [sceneSize[0], sceneSize[1], 1]
                );
                device.queue.submit([encoder.finish()]);
            }
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
            // Close any open scene-RT pass before encoding the copy.
            endOpenPass();
            const swapTex = context.getCurrentTexture();
            if (frameEncoder) {
                frameEncoder.copyTextureToTexture(
                    { texture: sceneColorTex },
                    { texture: swapTex },
                    [Math.min(sceneSize[0], swapTex.width), Math.min(sceneSize[1], swapTex.height), 1]
                );
            } else {
                const encoder = device.createCommandEncoder();
                encoder.copyTextureToTexture(
                    { texture: sceneColorTex },
                    { texture: swapTex },
                    [Math.min(sceneSize[0], swapTex.width), Math.min(sceneSize[1], swapTex.height), 1]
                );
                device.queue.submit([encoder.finish()]);
            }
            inSceneFrame = false;
        },

        renderOverlay(pipelineId, vertexBufferId, indexBufferId, bindGroupId, indexCount) {
            if (!device || !context) return;
            const pass = startPassIfNeeded('load', 'clear');
            if (pass) {
                const bg = bindGroups[bindGroupId];
                pass.setPipeline(pipelines[pipelineId]);
                pass.setVertexBuffer(0, buffers[vertexBufferId]);
                pass.setIndexBuffer(buffers[indexBufferId], indexFormats.get(indexBufferId) || 'uint16');
                pass.setBindGroup(0, bg);
                pass.drawIndexed(indexCount);
                noteRead(vertexBufferId); noteRead(indexBufferId);
                if (bg && bg.__bufferIds) for (const id of bg.__bufferIds) noteRead(id);
                return;
            }
            // Legacy fallback when BeginFrame wasn't called.
            const colorView = inSceneFrame
                ? sceneColorTex.createView()
                : context.getCurrentTexture().createView();
            const depthView = inSceneFrame
                ? sceneDepthTex.createView({ aspect: 'depth-only' })
                : ensureDepthTexture().createView();
            const encoder = device.createCommandEncoder();
            const p = encoder.beginRenderPass({
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
            p.setPipeline(pipelines[pipelineId]);
            p.setVertexBuffer(0, buffers[vertexBufferId]);
            p.setIndexBuffer(buffers[indexBufferId], indexFormats.get(indexBufferId) || 'uint16');
            p.setBindGroup(0, bindGroups[bindGroupId]);
            p.drawIndexed(indexCount);
            p.end();
            device.queue.submit([encoder.finish()]);
        },

        renderNoBind(pipelineId, vertexBufferId, indexBufferId, indexCount) {
            if (!device || !context) return;
            const pass = startPassIfNeeded('load', 'load');
            if (pass) {
                pass.setPipeline(pipelines[pipelineId]);
                pass.setVertexBuffer(0, buffers[vertexBufferId]);
                pass.setIndexBuffer(buffers[indexBufferId], indexFormats.get(indexBufferId) || 'uint16');
                pass.drawIndexed(indexCount);
                noteRead(vertexBufferId); noteRead(indexBufferId);
                return;
            }
            // Legacy fallback when BeginFrame wasn't called.
            const colorView = inSceneFrame
                ? sceneColorTex.createView()
                : context.getCurrentTexture().createView();
            const depthView = inSceneFrame
                ? sceneDepthTex.createView({ aspect: 'depth-only' })
                : ensureDepthTexture().createView();
            const encoder = device.createCommandEncoder();
            const p = encoder.beginRenderPass({
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
            p.setPipeline(pipelines[pipelineId]);
            p.setVertexBuffer(0, buffers[vertexBufferId]);
            p.setIndexBuffer(buffers[indexBufferId], indexFormats.get(indexBufferId) || 'uint16');
            p.drawIndexed(indexCount);
            p.end();
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
