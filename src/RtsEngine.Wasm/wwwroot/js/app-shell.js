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

            // Mouse — track drag vs click
            let dragging = false, lastX = 0, lastY = 0;
            let downX = 0, downY = 0, downButton = 0, totalDragDist = 0;
            const CLICK_THRESHOLD = 5; // pixels — below this, treat mouseup as click

            // Block context menu on canvas and its container
            canvas.addEventListener('contextmenu', e => { e.preventDefault(); e.stopPropagation(); });
            canvas.parentElement?.addEventListener?.('contextmenu', e => { e.preventDefault(); e.stopPropagation(); });

            canvas.addEventListener('mousedown', e => {
                e.preventDefault();
                dragging = true;
                lastX = e.clientX; lastY = e.clientY;
                downX = e.clientX; downY = e.clientY;
                downButton = e.button;
                totalDragDist = 0;
                if (e.button === 0) dotnetRef.invokeMethodAsync('OnPointerDown');
            });
            canvas.addEventListener('mousemove', e => {
                // Always send move for hover highlight
                const rect2 = canvas.getBoundingClientRect();
                const dpr2 = window.devicePixelRatio || 1;
                dotnetRef.invokeMethodAsync('OnPointerMove',
                    (e.clientX - rect2.left) * dpr2,
                    (e.clientY - rect2.top) * dpr2);

                if (!dragging) return;
                const dx = e.clientX - lastX, dy = e.clientY - lastY;
                totalDragDist += Math.abs(dx) + Math.abs(dy);
                if (downButton === 0) dotnetRef.invokeMethodAsync('OnPointerDrag', dx, dy);
                lastX = e.clientX; lastY = e.clientY;
            });
            canvas.addEventListener('mouseup', e => {
                if (!dragging) return;
                dragging = false;
                if (downButton === 0) dotnetRef.invokeMethodAsync('OnPointerUp');
                if (totalDragDist < CLICK_THRESHOLD) {
                    const rect = canvas.getBoundingClientRect();
                    const dpr = window.devicePixelRatio || 1;
                    const cx = (e.clientX - rect.left) * dpr;
                    const cy = (e.clientY - rect.top) * dpr;
                    dotnetRef.invokeMethodAsync('OnPointerClick', cx, cy, e.button);
                }
            });
            canvas.addEventListener('mouseleave', () => {
                if (dragging) { dragging = false; dotnetRef.invokeMethodAsync('OnPointerUp'); }
            });

            // Scroll
            canvas.addEventListener('wheel', e => {
                e.preventDefault();
                dotnetRef.invokeMethodAsync('OnScroll', -e.deltaY);
            }, { passive: false });

            // Keyboard
            document.addEventListener('keydown', e => {
                dotnetRef.invokeMethodAsync('OnKeyDown', e.key);
            });

            // Touch
            let touchActive = false, lastTX = 0, lastTY = 0;
            let touchDownX = 0, touchDownY = 0, touchDragDist = 0;
            canvas.addEventListener('touchstart', e => {
                e.preventDefault();
                if (e.touches.length === 1) {
                    touchActive = true;
                    lastTX = e.touches[0].clientX; lastTY = e.touches[0].clientY;
                    touchDownX = lastTX; touchDownY = lastTY; touchDragDist = 0;
                    dotnetRef.invokeMethodAsync('OnPointerDown');
                }
            }, { passive: false });
            canvas.addEventListener('touchmove', e => {
                e.preventDefault();
                if (!touchActive || e.touches.length !== 1) return;
                const dx = e.touches[0].clientX - lastTX, dy = e.touches[0].clientY - lastTY;
                touchDragDist += Math.abs(dx) + Math.abs(dy);
                dotnetRef.invokeMethodAsync('OnPointerDrag', dx, dy);
                lastTX = e.touches[0].clientX; lastTY = e.touches[0].clientY;
            }, { passive: false });
            canvas.addEventListener('touchend', e => {
                e.preventDefault();
                if (touchActive) {
                    touchActive = false;
                    dotnetRef.invokeMethodAsync('OnPointerUp');
                    if (touchDragDist < CLICK_THRESHOLD) {
                        const rect = canvas.getBoundingClientRect();
                        const dpr = window.devicePixelRatio || 1;
                        const cx = (touchDownX - rect.left) * dpr;
                        const cy = (touchDownY - rect.top) * dpr;
                        dotnetRef.invokeMethodAsync('OnPointerClick', cx, cy, 0);
                    }
                }
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
        },

        // ── Engine-managed UI buttons (HTML overlay, controlled by game code) ──
        createButton(id, text, cssJson) {
            let btn = document.getElementById('engine-btn-' + id);
            if (!btn) {
                btn = document.createElement('button');
                btn.id = 'engine-btn-' + id;
                btn.style.position = 'absolute';
                btn.style.zIndex = '100';
                btn.style.pointerEvents = 'auto';
                const container = document.getElementById('game-container');
                if (container) container.appendChild(btn);
                else document.body.appendChild(btn);
                btn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    if (dotnetRef) dotnetRef.invokeMethodAsync('OnUIButtonClick', id);
                });
            }
            btn.textContent = text;
            try { Object.assign(btn.style, JSON.parse(cssJson)); } catch {}
        },

        showButton(id, visible) {
            const btn = document.getElementById('engine-btn-' + id);
            if (btn) btn.style.display = visible ? 'block' : 'none';
        },

        removeButton(id) {
            const el = document.getElementById('engine-btn-' + id);
            if (el) el.remove();
        },
    };
})();
