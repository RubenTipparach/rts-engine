// WebGPU proxy — the Emscripten-equivalent translation layer for WebGPU.
// Maps integer handles to WebGPU objects, forwards calls 1:1.
// Infrastructure, not application code. Engine dev never touches this.

(() => {
    let device = null;
    let context = null;
    let canvasFormat = null;
    let canvas = null;

    // Handle tables (index 0 = null sentinel)
    const shaderModules = [null];
    const pipelines = [null];
    const buffers = [null];
    const bindGroups = [null];
    const bindGroupLayouts = [null];

    function register(table, obj) {
        const id = table.length;
        table.push(obj);
        return id;
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

        getCanvasFormat() {
            return canvasFormat;
        },

        resizeCanvas() {
            if (!canvas || !context || !device) return;
            const dpr = window.devicePixelRatio || 1;
            const rect = canvas.getBoundingClientRect();
            canvas.width  = Math.floor(rect.width  * dpr);
            canvas.height = Math.floor(rect.height * dpr);
            context.configure({ device, format: canvasFormat, alphaMode: 'premultiplied' });
        },

        // ── Shader Modules ───────────────────────────────────────
        createShaderModule(wgslCode) {
            const mod = device.createShaderModule({ code: wgslCode });
            return register(shaderModules, mod);
        },

        // ── Buffers ──────────────────────────────────────────────
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
            // Pad to 4-byte alignment
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
            const buf = device.createBuffer({
                size: sizeBytes,
                usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
            });
            return register(buffers, buf);
        },

        writeBuffer(bufferId, floatData) {
            const f32 = new Float32Array(floatData);
            device.queue.writeBuffer(buffers[bufferId], 0, f32);
        },

        // ── Pipeline ─────────────────────────────────────────────
        createRenderPipeline(shaderModuleId, vertexBufferLayouts) {
            const pipeline = device.createRenderPipeline({
                layout: 'auto',
                vertex: {
                    module: shaderModules[shaderModuleId],
                    entryPoint: 'vs_main',
                    buffers: vertexBufferLayouts.map(layout => ({
                        arrayStride: layout.arrayStride,
                        attributes: layout.attributes.map(attr => ({
                            format: attr.format,
                            offset: attr.offset,
                            shaderLocation: attr.shaderLocation,
                        })),
                    })),
                },
                fragment: {
                    module: shaderModules[shaderModuleId],
                    entryPoint: 'fs_main',
                    targets: [{ format: canvasFormat }],
                },
                primitive: {
                    topology: 'triangle-list',
                    cullMode: 'back',
                },
                depthStencil: {
                    format: 'depth24plus',
                    depthWriteEnabled: true,
                    depthCompare: 'less',
                },
            });
            return register(pipelines, pipeline);
        },

        // ── Bind Groups ──────────────────────────────────────────
        createBindGroup(pipelineId, groupIndex, entries) {
            const pipeline = pipelines[pipelineId];
            const bg = device.createBindGroup({
                layout: pipeline.getBindGroupLayout(groupIndex),
                entries: entries.map(e => ({
                    binding: e.binding,
                    resource: { buffer: buffers[e.bufferId] },
                })),
            });
            return register(bindGroups, bg);
        },

        // ── Render ───────────────────────────────────────────────
        render(pipelineId, vertexBufferId, indexBufferId, bindGroupId, indexCount) {
            if (!device || !context) return;

            const depthTexture = device.createTexture({
                size: [canvas.width, canvas.height],
                format: 'depth24plus',
                usage: GPUTextureUsage.RENDER_ATTACHMENT,
            });

            const encoder = device.createCommandEncoder();
            const pass = encoder.beginRenderPass({
                colorAttachments: [{
                    view: context.getCurrentTexture().createView(),
                    clearValue: { r: 0.05, g: 0.05, b: 0.12, a: 1.0 },
                    loadOp: 'clear',
                    storeOp: 'store',
                }],
                depthStencilAttachment: {
                    view: depthTexture.createView(),
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
            depthTexture.destroy();
        },

        // ── Cleanup ──────────────────────────────────────────────
        destroyBuffer(id) {
            if (buffers[id]) { buffers[id].destroy(); buffers[id] = null; }
        },
    };
})();
