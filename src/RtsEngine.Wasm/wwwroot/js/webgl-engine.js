// WebGL Spinning Cube Engine
// Handles all WebGL rendering, called from C# via JS interop

let gl = null;
let shaderProgram = null;
let vertexBuffer = null;
let indexBuffer = null;
let mvpUniformLocation = null;
let animFrameId = null;
let dotnetRef = null;

// Cube vertex data: position (x,y,z) + color (r,g,b)
const CUBE_VERTICES = new Float32Array([
    // Front face (red)
    -1, -1,  1,   1.0, 0.2, 0.2,
     1, -1,  1,   1.0, 0.2, 0.2,
     1,  1,  1,   1.0, 0.4, 0.4,
    -1,  1,  1,   1.0, 0.4, 0.4,
    // Back face (green)
    -1, -1, -1,   0.2, 1.0, 0.2,
    -1,  1, -1,   0.2, 1.0, 0.4,
     1,  1, -1,   0.4, 1.0, 0.4,
     1, -1, -1,   0.4, 1.0, 0.2,
    // Top face (blue)
    -1,  1, -1,   0.2, 0.2, 1.0,
    -1,  1,  1,   0.2, 0.4, 1.0,
     1,  1,  1,   0.4, 0.4, 1.0,
     1,  1, -1,   0.4, 0.2, 1.0,
    // Bottom face (yellow)
    -1, -1, -1,   1.0, 1.0, 0.2,
     1, -1, -1,   1.0, 1.0, 0.4,
     1, -1,  1,   1.0, 1.0, 0.4,
    -1, -1,  1,   1.0, 1.0, 0.2,
    // Right face (magenta)
     1, -1, -1,   1.0, 0.2, 1.0,
     1,  1, -1,   1.0, 0.4, 1.0,
     1,  1,  1,   1.0, 0.4, 1.0,
     1, -1,  1,   1.0, 0.2, 1.0,
    // Left face (cyan)
    -1, -1, -1,   0.2, 1.0, 1.0,
    -1, -1,  1,   0.2, 1.0, 1.0,
    -1,  1,  1,   0.4, 1.0, 1.0,
    -1,  1, -1,   0.4, 1.0, 1.0,
]);

const CUBE_INDICES = new Uint16Array([
    0,  1,  2,    0,  2,  3,   // front
    4,  5,  6,    4,  6,  7,   // back
    8,  9,  10,   8,  10, 11,  // top
    12, 13, 14,   12, 14, 15,  // bottom
    16, 17, 18,   16, 18, 19,  // right
    20, 21, 22,   20, 22, 23,  // left
]);

const VERTEX_SHADER_SRC = `
    attribute vec3 aPosition;
    attribute vec3 aColor;
    uniform mat4 uMVP;
    varying vec3 vColor;
    void main() {
        gl_Position = uMVP * vec4(aPosition, 1.0);
        vColor = aColor;
    }
`;

const FRAGMENT_SHADER_SRC = `
    precision mediump float;
    varying vec3 vColor;
    void main() {
        gl_FragColor = vec4(vColor, 1.0);
    }
`;

function compileShader(type, source) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
        console.error('Shader compile error:', gl.getShaderInfoLog(shader));
        gl.deleteShader(shader);
        return null;
    }
    return shader;
}

window.WebGLEngine = {
    init: function (canvasId, dotnetObjRef) {
        dotnetRef = dotnetObjRef;
        const canvas = document.getElementById(canvasId);
        if (!canvas) {
            console.error('Canvas not found:', canvasId);
            return false;
        }

        gl = canvas.getContext('webgl') || canvas.getContext('experimental-webgl');
        if (!gl) {
            console.error('WebGL not supported');
            return false;
        }

        // Compile shaders
        const vertShader = compileShader(gl.VERTEX_SHADER, VERTEX_SHADER_SRC);
        const fragShader = compileShader(gl.FRAGMENT_SHADER, FRAGMENT_SHADER_SRC);
        if (!vertShader || !fragShader) return false;

        // Link program
        shaderProgram = gl.createProgram();
        gl.attachShader(shaderProgram, vertShader);
        gl.attachShader(shaderProgram, fragShader);
        gl.linkProgram(shaderProgram);
        if (!gl.getProgramParameter(shaderProgram, gl.LINK_STATUS)) {
            console.error('Shader link error:', gl.getProgramInfoLog(shaderProgram));
            return false;
        }
        gl.useProgram(shaderProgram);

        // Create buffers
        vertexBuffer = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, vertexBuffer);
        gl.bufferData(gl.ARRAY_BUFFER, CUBE_VERTICES, gl.STATIC_DRAW);

        indexBuffer = gl.createBuffer();
        gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, indexBuffer);
        gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, CUBE_INDICES, gl.STATIC_DRAW);

        // Set up vertex attributes
        const stride = 6 * 4; // 6 floats * 4 bytes
        const posAttrib = gl.getAttribLocation(shaderProgram, 'aPosition');
        gl.enableVertexAttribArray(posAttrib);
        gl.vertexAttribPointer(posAttrib, 3, gl.FLOAT, false, stride, 0);

        const colorAttrib = gl.getAttribLocation(shaderProgram, 'aColor');
        gl.enableVertexAttribArray(colorAttrib);
        gl.vertexAttribPointer(colorAttrib, 3, gl.FLOAT, false, stride, 3 * 4);

        mvpUniformLocation = gl.getUniformLocation(shaderProgram, 'uMVP');

        // GL state
        gl.enable(gl.DEPTH_TEST);
        gl.clearColor(0.05, 0.05, 0.12, 1.0);

        // Handle resize
        this.resize(canvasId);

        // Input events
        this._setupInput(canvas);

        return true;
    },

    resize: function (canvasId) {
        const canvas = document.getElementById(canvasId);
        if (!canvas || !gl) return;

        const dpr = window.devicePixelRatio || 1;
        const rect = canvas.getBoundingClientRect();
        canvas.width = rect.width * dpr;
        canvas.height = rect.height * dpr;
        gl.viewport(0, 0, canvas.width, canvas.height);
    },

    render: function (mvpArray) {
        if (!gl || !shaderProgram) return;

        gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
        gl.uniformMatrix4fv(mvpUniformLocation, false, new Float32Array(mvpArray));
        gl.drawElements(gl.TRIANGLES, 36, gl.UNSIGNED_SHORT, 0);
    },

    _setupInput: function (canvas) {
        // Mouse events
        let isDragging = false;
        let lastX = 0, lastY = 0;

        canvas.addEventListener('mousedown', (e) => {
            isDragging = true;
            lastX = e.clientX;
            lastY = e.clientY;
            e.preventDefault();
        });

        canvas.addEventListener('mousemove', (e) => {
            if (!isDragging || !dotnetRef) return;
            const dx = e.clientX - lastX;
            const dy = e.clientY - lastY;
            lastX = e.clientX;
            lastY = e.clientY;
            dotnetRef.invokeMethodAsync('OnPointerDrag', dx, dy);
        });

        canvas.addEventListener('mouseup', () => { isDragging = false; });
        canvas.addEventListener('mouseleave', () => { isDragging = false; });

        // Mouse wheel for zoom/velocity boost
        canvas.addEventListener('wheel', (e) => {
            e.preventDefault();
            if (dotnetRef) {
                dotnetRef.invokeMethodAsync('OnScrollWheel', e.deltaY);
            }
        }, { passive: false });

        // Touch events
        let lastTouchX = 0, lastTouchY = 0;
        let touchActive = false;

        canvas.addEventListener('touchstart', (e) => {
            e.preventDefault();
            if (e.touches.length === 1) {
                touchActive = true;
                lastTouchX = e.touches[0].clientX;
                lastTouchY = e.touches[0].clientY;
                if (dotnetRef) {
                    dotnetRef.invokeMethodAsync('OnTapStart');
                }
            }
        }, { passive: false });

        canvas.addEventListener('touchmove', (e) => {
            e.preventDefault();
            if (!touchActive || e.touches.length !== 1 || !dotnetRef) return;
            const dx = e.touches[0].clientX - lastTouchX;
            const dy = e.touches[0].clientY - lastTouchY;
            lastTouchX = e.touches[0].clientX;
            lastTouchY = e.touches[0].clientY;
            dotnetRef.invokeMethodAsync('OnPointerDrag', dx, dy);
        }, { passive: false });

        canvas.addEventListener('touchend', (e) => {
            e.preventDefault();
            touchActive = false;
        }, { passive: false });

        // Double click/tap to reset
        canvas.addEventListener('dblclick', (e) => {
            e.preventDefault();
            if (dotnetRef) {
                dotnetRef.invokeMethodAsync('OnReset');
            }
        });
    },

    startLoop: function () {
        const loop = () => {
            if (dotnetRef) {
                dotnetRef.invokeMethodAsync('GameLoopTick');
            }
            animFrameId = requestAnimationFrame(loop);
        };
        animFrameId = requestAnimationFrame(loop);
    },

    stopLoop: function () {
        if (animFrameId) {
            cancelAnimationFrame(animFrameId);
            animFrameId = null;
        }
    },

    dispose: function () {
        this.stopLoop();
        if (gl) {
            gl.deleteBuffer(vertexBuffer);
            gl.deleteBuffer(indexBuffer);
            gl.deleteProgram(shaderProgram);
        }
        gl = null;
        dotnetRef = null;
    }
};
