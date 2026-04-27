// WebGPU proxy — the Emscripten-equivalent translation layer for WebGPU.
// Maps integer handles to WebGPU objects, forwards calls 1:1.

(() => {
    let device = null;
    let context = null;
    let canvasFormat = null;
    let canvas = null;
    let depthTexture = null;
    let depthTextureSize = [0, 0];

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
                if (!canvas) return false;
                context = canvas.getContext('webgpu');
                if (!context) {
                    window.GPUProxyInitError = 'Failed to get WebGPU canvas context.';
                    return false;
                }
                canvasFormat = navigator.gpu.getPreferredCanvasFormat();
                context.configure({ device, format: canvasFormat, alphaMode: 'premultiplied' });
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
            new Uint16Array(buf.getMappedRange(0, u16.byteLength)).set(u16);
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

        render(pipelineId, vertexBufferId, indexBufferId, bindGroupId, indexCount) {
            if (!device || !context) return;
            const depthTex = ensureDepthTexture();

            const encoder = device.createCommandEncoder();
            const pass = encoder.beginRenderPass({
                colorAttachments: [{
                    view: context.getCurrentTexture().createView(),
                    clearValue: { r: 0.02, g: 0.02, b: 0.06, a: 1.0 },
                    loadOp: 'clear',
                    storeOp: 'store',
                }],
                depthStencilAttachment: {
                    view: depthTex.createView(),
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
            const depthTex = ensureDepthTexture();
            const encoder = device.createCommandEncoder();
            const pass = encoder.beginRenderPass({
                colorAttachments: [{
                    view: context.getCurrentTexture().createView(),
                    loadOp: 'load',
                    storeOp: 'store',
                }],
                depthStencilAttachment: {
                    view: depthTex.createView(),
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
                    targets: [{ format: canvasFormat }],
                },
                primitive: { topology: 'line-list' },
                depthStencil: { format: 'depth24plus', depthWriteEnabled: false, depthCompare: 'less-equal' },
            });
            return register(pipelines, pipeline);
        },

        renderNoBind(pipelineId, vertexBufferId, indexBufferId, indexCount) {
            if (!device || !context) return;
            const depthTex = ensureDepthTexture();
            const encoder = device.createCommandEncoder();
            const pass = encoder.beginRenderPass({
                colorAttachments: [{
                    view: context.getCurrentTexture().createView(),
                    loadOp: 'load',
                    storeOp: 'store',
                }],
                depthStencilAttachment: {
                    view: depthTex.createView(),
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
