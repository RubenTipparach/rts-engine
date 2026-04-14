// Generic GL → WebGL proxy.
//
// This is the equivalent of Emscripten's GL translation layer.
// It maps integer handles to WebGL objects and forwards every call 1:1.
// Written once, never modified by engine code — it's infrastructure.
//
// C# GL.createBuffer()  →  GLProxy.createBuffer()  →  gl.createBuffer()
// C# GL.drawElements()  →  GLProxy.drawElements()  →  gl.drawElements()

(() => {
    let gl = null;

    // Handle tables — WebGL uses object refs, C# needs integer IDs.
    // Same pattern Emscripten uses internally.
    const shaders = [null];   // index 0 = null sentinel
    const programs = [null];
    const buffers = [null];
    const uniformLocs = [null];

    function register(table, obj) {
        const id = table.length;
        table.push(obj);
        return id;
    }

    window.GLProxy = {
        init(canvasId) {
            const canvas = document.getElementById(canvasId);
            if (!canvas) return false;
            gl = canvas.getContext('webgl') || canvas.getContext('experimental-webgl');
            return !!gl;
        },

        // ── State ─────────────────────────────────────────────
        enable(cap)                     { gl.enable(cap); },
        clearColor(r, g, b, a)         { gl.clearColor(r, g, b, a); },
        clear(mask)                     { gl.clear(mask); },
        viewport(x, y, w, h)           { gl.viewport(x, y, w, h); },

        // ── Shaders ───────────────────────────────────────────
        createShader(type) {
            return register(shaders, gl.createShader(type));
        },
        shaderSource(id, src)           { gl.shaderSource(shaders[id], src); },
        compileShader(id)               { gl.compileShader(shaders[id]); },
        getShaderParameter(id, pname)   { return gl.getShaderParameter(shaders[id], pname); },
        getShaderInfoLog(id)            { return gl.getShaderInfoLog(shaders[id]) || ''; },
        deleteShader(id)                { gl.deleteShader(shaders[id]); shaders[id] = null; },

        // ── Programs ──────────────────────────────────────────
        createProgram() {
            return register(programs, gl.createProgram());
        },
        attachShader(prog, sh)          { gl.attachShader(programs[prog], shaders[sh]); },
        linkProgram(id)                 { gl.linkProgram(programs[id]); },
        getProgramParameter(id, pname)  { return gl.getProgramParameter(programs[id], pname); },
        getProgramInfoLog(id)           { return gl.getProgramInfoLog(programs[id]) || ''; },
        useProgram(id)                  { gl.useProgram(programs[id]); },
        deleteProgram(id)               { gl.deleteProgram(programs[id]); programs[id] = null; },

        // ── Buffers ───────────────────────────────────────────
        createBuffer() {
            return register(buffers, gl.createBuffer());
        },
        bindBuffer(target, id)          { gl.bindBuffer(target, buffers[id]); },
        bufferDataFloat(target, data, usage) {
            gl.bufferData(target, new Float32Array(data), usage);
        },
        bufferDataUshort(target, data, usage) {
            gl.bufferData(target, new Uint16Array(data), usage);
        },
        deleteBuffer(id)                { gl.deleteBuffer(buffers[id]); buffers[id] = null; },

        // ── Vertex attributes ─────────────────────────────────
        getAttribLocation(prog, name)   { return gl.getAttribLocation(programs[prog], name); },
        enableVertexAttribArray(idx)    { gl.enableVertexAttribArray(idx); },
        vertexAttribPointer(idx, size, type, norm, stride, offset) {
            gl.vertexAttribPointer(idx, size, type, norm, stride, offset);
        },

        // ── Uniforms ──────────────────────────────────────────
        getUniformLocation(prog, name) {
            return register(uniformLocs, gl.getUniformLocation(programs[prog], name));
        },
        uniformMatrix4fv(loc, transpose, value) {
            gl.uniformMatrix4fv(uniformLocs[loc], transpose, new Float32Array(value));
        },

        // ── Draw ──────────────────────────────────────────────
        drawElements(mode, count, type, offset) {
            gl.drawElements(mode, count, type, offset);
        }
    };
})();
