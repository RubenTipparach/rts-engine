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
            if (!navigator.gpu) {
                console.error('WebGPU not supported');
                return false;
            }
            const adapter = await navigator.gpu.requestAdapter();
            if (!adapter) {
                console.error('No GPU adapter found');
                return false;
            }
            device = await adapter.requestDevice();
            canvas = document.getElementById(canvasId);
            if (!canvas) return false;
            context = canvas.getContext('webgpu');
            canvasFormat = navigator.gpu.getPreferredCanvasFormat();
            context.configure({ device, format: canvasFormat, alphaMode: 'premultiplied' });
            return true;
        },

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
            return register(shaderModules, device.createShaderModule({ code: wgslCode }));
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
            const padded = u16.byteLength % 4 === 0 ? u16.byteLength : u16.byteLength + 2;
            const buf = device.createBuffer({
                size: padded,
                usage: GPUBufferUsage.INDEX | GPUBufferUsage.COPY_DST,
                mappedAtCreation: true,
            });
            new Uint16Array(buf.getMappedRange(0, u16.byteLength)).set(u16);
            buf.unmap();
            return register(buffers, buf);
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
            pass.setIndexBuffer(buffers[indexBufferId], 'uint16');
            pass.setBindGroup(0, bindGroups[bindGroupId]);
            pass.drawIndexed(indexCount);
            pass.end();

            device.queue.submit([encoder.finish()]);
        },

        destroyBuffer(id) {
            if (buffers[id]) { buffers[id].destroy(); buffers[id] = null; }
        },

        // ── Textures / Samplers ─────────────────────────────────

        async createTextureFromUrl(url) {
            const resp = await fetch(url);
            const blob = await resp.blob();
            const bitmap = await createImageBitmap(blob);
            const tex = device.createTexture({
                size: [bitmap.width, bitmap.height, 1],
                format: 'rgba8unorm',
                usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST | GPUTextureUsage.RENDER_ATTACHMENT,
            });
            device.queue.copyExternalImageToTexture(
                { source: bitmap },
                { texture: tex },
                [bitmap.width, bitmap.height, 1]
            );
            const view = tex.createView();
            textures.push(tex);
            return register(textureViews, view);
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
