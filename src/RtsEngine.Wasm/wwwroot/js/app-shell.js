// App shell — equivalent to sokol_app for WASM.
// Manages canvas sizing, the frame loop, and raw input forwarding.
// Does NOT do any GPU calls — those go through gpu-proxy.js.

(() => {
    let animFrameId = null;
    let dotnetRef = null;

    window.AppShell = {
        init(canvasId, dotnetObjRef) {
            dotnetRef = dotnetObjRef;
            const canvas = document.getElementById(canvasId);
            if (!canvas) return;

            // Resize
            const resize = () => {
                const dpr = window.devicePixelRatio || 1;
                const rect = canvas.getBoundingClientRect();
                canvas.width = Math.floor(rect.width * dpr);
                canvas.height = Math.floor(rect.height * dpr);
                if (window.GPUProxy) GPUProxy.resizeCanvas();
                dotnetRef.invokeMethodAsync('OnCanvasResize', canvas.width, canvas.height);
            };
            new ResizeObserver(resize).observe(canvas);
            resize();

            // Mouse
            let dragging = false, lastX = 0, lastY = 0;
            canvas.addEventListener('mousedown', e => {
                dragging = true; lastX = e.clientX; lastY = e.clientY;
                dotnetRef.invokeMethodAsync('OnPointerDown');
                e.preventDefault();
            });
            canvas.addEventListener('mousemove', e => {
                if (!dragging) return;
                dotnetRef.invokeMethodAsync('OnPointerDrag', e.clientX - lastX, e.clientY - lastY);
                lastX = e.clientX; lastY = e.clientY;
            });
            canvas.addEventListener('mouseup', () => { if (dragging) { dragging = false; dotnetRef.invokeMethodAsync('OnPointerUp'); } });
            canvas.addEventListener('mouseleave', () => { if (dragging) { dragging = false; dotnetRef.invokeMethodAsync('OnPointerUp'); } });

            // Touch
            let touchActive = false, lastTX = 0, lastTY = 0;
            canvas.addEventListener('touchstart', e => {
                e.preventDefault();
                if (e.touches.length === 1) {
                    touchActive = true;
                    lastTX = e.touches[0].clientX; lastTY = e.touches[0].clientY;
                    dotnetRef.invokeMethodAsync('OnPointerDown');
                }
            }, { passive: false });
            canvas.addEventListener('touchmove', e => {
                e.preventDefault();
                if (!touchActive || e.touches.length !== 1) return;
                dotnetRef.invokeMethodAsync('OnPointerDrag',
                    e.touches[0].clientX - lastTX, e.touches[0].clientY - lastTY);
                lastTX = e.touches[0].clientX; lastTY = e.touches[0].clientY;
            }, { passive: false });
            canvas.addEventListener('touchend', e => {
                e.preventDefault();
                if (touchActive) { touchActive = false; dotnetRef.invokeMethodAsync('OnPointerUp'); }
            }, { passive: false });
        },

        startLoop() {
            const loop = () => {
                if (dotnetRef) dotnetRef.invokeMethodAsync('GameLoopTick');
                animFrameId = requestAnimationFrame(loop);
            };
            animFrameId = requestAnimationFrame(loop);
        },

        stopLoop() {
            if (animFrameId) { cancelAnimationFrame(animFrameId); animFrameId = null; }
        },

        dispose() {
            this.stopLoop();
            dotnetRef = null;
        }
    };
})();
