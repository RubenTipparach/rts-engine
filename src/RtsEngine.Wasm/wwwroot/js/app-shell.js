// App shell — equivalent to sokol_app for WASM.
// Manages canvas sizing, the frame loop, and raw input forwarding.
// Does NOT do any GPU calls — those go through gpu-proxy.js.
//
// Input semantics mirror DesktopAppBackend.cs so the engine sees the same
// intent vocabulary on both platforms (game code never branches on platform):
//
//   left click            → PointerClick(button=0)
//   left drag             → BoxSelectUpdate per move + BoxSelectComplete on release
//   left drag + Alt held  → OrbitDrag (camera orbit) + PointerDrag (legacy)
//   middle drag           → OrbitDrag + PointerDrag
//   right click (short)   → PointerClick(button=2)        (move/attack order)
//   right hold (long)     → ContextMenuRequested          (unit context menu)
//
//   1-finger drag                        → BoxSelectUpdate / BoxSelectComplete
//   1-finger drag while Pan button held  → OrbitDrag + PointerDrag (orbit)
//   2-finger pinch                       → Scroll (zoom)
//   tap (no drag)                        → PointerClick(button=0)

(() => {
    let animFrameId = null;
    let dotnetRef = null;

    // Match DesktopAppBackend's tuning so gestures feel identical.
    const CLICK_THRESHOLD = 5;   // px before a press is reclassified as a drag
    const LONG_PRESS_MS = 350;   // right-button hold to trigger context menu

    window.AppShell = {
        init(canvasId, dotnetObjRef) {
            dotnetRef = dotnetObjRef;
            const canvas = document.getElementById(canvasId);
            if (!canvas) return;

            // ── Resize ────────────────────────────────────────────
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

            // Translate a clientX/clientY pair to device-pixel canvas coords.
            const toCanvas = (cx, cy) => {
                const rect = canvas.getBoundingClientRect();
                const dpr = window.devicePixelRatio || 1;
                return [(cx - rect.left) * dpr, (cy - rect.top) * dpr];
            };

            // Block context menu on canvas and its container so right-press
            // long-hold can pop our own ContextMenuRequested instead.
            canvas.addEventListener('contextmenu', e => { e.preventDefault(); e.stopPropagation(); });
            canvas.parentElement?.addEventListener?.('contextmenu', e => { e.preventDefault(); e.stopPropagation(); });

            // ── Modifier state ────────────────────────────────────
            let altHeld = false;
            document.addEventListener('keydown', e => {
                if (e.key === 'Alt') altHeld = true;
                dotnetRef.invokeMethodAsync('OnKeyDown', e.key);
            });
            document.addEventListener('keyup', e => {
                if (e.key === 'Alt') altHeld = false;
            });

            // ── Mouse: per-button state, mirrors DesktopAppBackend ──
            // Each button independently tracks down/last/total-distance/
            // dragging/consumedAsOrbit so e.g. holding left + tapping middle
            // doesn't tangle their roles.
            const makeBtn = () => ({
                down: false,
                lastX: 0, lastY: 0,
                downX: 0, downY: 0,
                downTime: 0,
                totalDist: 0,
                dragging: false,
                consumedAsOrbit: false,
            });
            const left = makeBtn(), middle = makeBtn(), right = makeBtn();
            const pickBtn = (b) => b === 0 ? left : b === 1 ? middle : right;

            canvas.addEventListener('mousedown', e => {
                e.preventDefault();
                const s = pickBtn(e.button);
                s.down = true;
                s.lastX = e.clientX; s.lastY = e.clientY;
                s.downX = e.clientX; s.downY = e.clientY;
                s.downTime = performance.now();
                s.totalDist = 0;
                s.dragging = false;
                // Middle = always orbit. Alt+left = orbit. Plain left = box-select.
                s.consumedAsOrbit = e.button === 1 || (e.button === 0 && altHeld);
                if (e.button === 0) dotnetRef.invokeMethodAsync('OnPointerDown');
            });

            canvas.addEventListener('mousemove', e => {
                // Always send move for hover highlight, regardless of buttons.
                const [cx, cy] = toCanvas(e.clientX, e.clientY);
                dotnetRef.invokeMethodAsync('OnPointerMove', cx, cy);

                updateMouseDrag(left,   0, e.clientX, e.clientY);
                updateMouseDrag(middle, 1, e.clientX, e.clientY);
                // Right has no drag semantics (matches desktop), but track
                // distance so we can suppress click on accidental drags.
                if (right.down) {
                    const dx = e.clientX - right.lastX, dy = e.clientY - right.lastY;
                    right.totalDist += Math.abs(dx) + Math.abs(dy);
                    right.lastX = e.clientX; right.lastY = e.clientY;
                    if (right.totalDist >= CLICK_THRESHOLD) right.dragging = true;
                }
            });

            function updateMouseDrag(s, button, x, y) {
                if (!s.down) return;
                const dx = x - s.lastX, dy = y - s.lastY;
                s.totalDist += Math.abs(dx) + Math.abs(dy);
                s.lastX = x; s.lastY = y;
                if (s.totalDist >= CLICK_THRESHOLD) s.dragging = true;
                if (!s.dragging) return;

                if (s.consumedAsOrbit) {
                    // Mirror desktop: fire BOTH so legacy listeners reading
                    // PointerDrag still get camera input. Pre-existing double-
                    // fire in PlanetEdit (orbit listener + PointerDrag listener
                    // both call DispatchOrbit) is shared with desktop.
                    dotnetRef.invokeMethodAsync('OnOrbitDrag', dx, dy);
                    dotnetRef.invokeMethodAsync('OnPointerDrag', dx, dy);
                } else if (button === 0) {
                    // Plain-left drag → box-select. Coords in device pixels.
                    const [x0, y0] = toCanvas(s.downX, s.downY);
                    const [x1, y1] = toCanvas(x, y);
                    dotnetRef.invokeMethodAsync('OnBoxSelectUpdate', x0, y0, x1, y1);
                }
            }

            canvas.addEventListener('mouseup', e => {
                const s = pickBtn(e.button);
                if (!s.down) return;
                s.down = false;
                const heldMs = performance.now() - s.downTime;

                if (e.button === 0) {
                    dotnetRef.invokeMethodAsync('OnPointerUp');
                    if (s.dragging && !s.consumedAsOrbit) {
                        const x0 = Math.min(s.downX, e.clientX);
                        const y0 = Math.min(s.downY, e.clientY);
                        const x1 = Math.max(s.downX, e.clientX);
                        const y1 = Math.max(s.downY, e.clientY);
                        const [cx0, cy0] = toCanvas(x0, y0);
                        const [cx1, cy1] = toCanvas(x1, y1);
                        dotnetRef.invokeMethodAsync('OnBoxSelectComplete', cx0, cy0, cx1, cy1);
                        return;
                    }
                    if (s.dragging) return; // consumed as orbit; no click semantics
                    const [cx, cy] = toCanvas(e.clientX, e.clientY);
                    dotnetRef.invokeMethodAsync('OnPointerClick', cx, cy, 0);
                } else if (e.button === 2) {
                    if (s.dragging) return; // accidental drag — ignore
                    const [cx, cy] = toCanvas(e.clientX, e.clientY);
                    if (heldMs >= LONG_PRESS_MS)
                        dotnetRef.invokeMethodAsync('OnContextMenuRequested', cx, cy);
                    else
                        dotnetRef.invokeMethodAsync('OnPointerClick', cx, cy, 2);
                }
                // Middle up: nothing — its drag was already routed as orbit.
            });

            canvas.addEventListener('mouseleave', () => {
                // Treat as a cancel on every held button so we don't get stuck
                // dragging when the cursor leaves the canvas.
                if (left.down)   { left.down   = false; dotnetRef.invokeMethodAsync('OnPointerUp'); }
                if (middle.down) { middle.down = false; }
                if (right.down)  { right.down  = false; }
            });

            // ── Scroll ────────────────────────────────────────────
            canvas.addEventListener('wheel', e => {
                e.preventDefault();
                dotnetRef.invokeMethodAsync('OnScroll', -e.deltaY);
            }, { passive: false });

            // ── Pan button (touch-only camera-orbit modifier) ──────
            // Held = next single-finger drag emits OrbitDrag instead of
            // BoxSelect. Lives entirely in JS; game code never sees it. CSS
            // hides on fine-pointer (mouse) devices since they have alt+left
            // and middle-drag for the same intent.
            let panHeld = false;
            const panBtn = document.createElement('button');
            panBtn.id = 'engine-pan-btn';
            panBtn.type = 'button';
            panBtn.textContent = '🎥';
            panBtn.setAttribute('aria-label', 'Hold to pan camera');
            Object.assign(panBtn.style, {
                position: 'absolute',
                right: '16px',
                bottom: '16px',
                width: '64px',
                height: '64px',
                borderRadius: '50%',
                border: 'none',
                background: 'rgba(20, 30, 50, 0.75)',
                color: 'white',
                fontSize: '28px',
                zIndex: '200',
                touchAction: 'none',
                userSelect: 'none',
                webkitUserSelect: 'none',
                pointerEvents: 'auto',
                boxShadow: '0 2px 8px rgba(0,0,0,0.4)',
            });
            // Inject a media-query stylesheet that hides the button on
            // fine-pointer devices (desktop with mouse). Touch / hybrid keep
            // it. Inline styles can't express @media so we use a <style>.
            if (!document.getElementById('engine-pan-btn-style')) {
                const style = document.createElement('style');
                style.id = 'engine-pan-btn-style';
                style.textContent =
                    '@media (hover: hover) and (pointer: fine) { #engine-pan-btn { display: none !important; } }' +
                    '#engine-pan-btn.active { background: rgba(80, 160, 255, 0.9) !important; }';
                document.head.appendChild(style);
            }
            const container = document.getElementById('game-container') || document.body;
            container.appendChild(panBtn);

            const setPanHeld = (held) => {
                panHeld = held;
                panBtn.classList.toggle('active', held);
            };
            panBtn.addEventListener('pointerdown', e => {
                e.preventDefault();
                e.stopPropagation();
                panBtn.setPointerCapture?.(e.pointerId);
                setPanHeld(true);
            });
            const releasePan = (e) => {
                if (e) panBtn.releasePointerCapture?.(e.pointerId);
                setPanHeld(false);
            };
            panBtn.addEventListener('pointerup', releasePan);
            panBtn.addEventListener('pointercancel', releasePan);
            panBtn.addEventListener('pointerleave', releasePan);

            // ── Touch ─────────────────────────────────────────────
            // 1-finger:  pan held → OrbitDrag.   pan free → BoxSelect.
            // 2-finger:  pinch zoom (existing).
            // Tap (no movement past CLICK_THRESHOLD) → PointerClick(0).
            let touchActive = false, lastTX = 0, lastTY = 0;
            let touchDownX = 0, touchDownY = 0, touchDragDist = 0;
            let touchModeOrbit = false;   // captured at touchstart
            let pinchActive = false, lastPinchDist = 0;
            const pinchDist = (t) => {
                const dx = t[0].clientX - t[1].clientX;
                const dy = t[0].clientY - t[1].clientY;
                return Math.hypot(dx, dy);
            };

            canvas.addEventListener('touchstart', e => {
                e.preventDefault();
                if (e.touches.length === 1 && !pinchActive) {
                    touchActive = true;
                    lastTX = e.touches[0].clientX; lastTY = e.touches[0].clientY;
                    touchDownX = lastTX; touchDownY = lastTY; touchDragDist = 0;
                    touchModeOrbit = panHeld;
                    if (!touchModeOrbit) dotnetRef.invokeMethodAsync('OnPointerDown');
                } else if (e.touches.length === 2) {
                    if (touchActive) {
                        touchActive = false;
                        if (!touchModeOrbit) dotnetRef.invokeMethodAsync('OnPointerUp');
                    }
                    pinchActive = true;
                    lastPinchDist = pinchDist(e.touches);
                }
            }, { passive: false });

            canvas.addEventListener('touchmove', e => {
                e.preventDefault();
                if (pinchActive && e.touches.length === 2) {
                    const d = pinchDist(e.touches);
                    const delta = (d - lastPinchDist) * 5;
                    lastPinchDist = d;
                    if (Math.abs(delta) > 0.01) dotnetRef.invokeMethodAsync('OnScroll', delta);
                    return;
                }
                if (!touchActive || e.touches.length !== 1) return;
                const x = e.touches[0].clientX, y = e.touches[0].clientY;
                const dx = x - lastTX, dy = y - lastTY;
                touchDragDist += Math.abs(dx) + Math.abs(dy);
                lastTX = x; lastTY = y;
                if (touchDragDist < CLICK_THRESHOLD) return;

                if (touchModeOrbit) {
                    // Mirror desktop alt+left: emit both OrbitDrag and
                    // PointerDrag so legacy paths (solar system / star map)
                    // that read PointerDrag still get camera input.
                    dotnetRef.invokeMethodAsync('OnOrbitDrag', dx, dy);
                    dotnetRef.invokeMethodAsync('OnPointerDrag', dx, dy);
                } else {
                    const [x0, y0] = toCanvas(touchDownX, touchDownY);
                    const [x1, y1] = toCanvas(x, y);
                    dotnetRef.invokeMethodAsync('OnBoxSelectUpdate', x0, y0, x1, y1);
                }
            }, { passive: false });

            canvas.addEventListener('touchend', e => {
                e.preventDefault();
                if (pinchActive && e.touches.length < 2) {
                    pinchActive = false;
                    return;
                }
                if (!touchActive) return;
                touchActive = false;

                if (touchModeOrbit) {
                    // Orbit gesture: no PointerUp / click semantics — matches
                    // mouse middle-drag which never fires PointerDown/Up.
                    return;
                }
                dotnetRef.invokeMethodAsync('OnPointerUp');
                if (touchDragDist < CLICK_THRESHOLD) {
                    const [cx, cy] = toCanvas(touchDownX, touchDownY);
                    dotnetRef.invokeMethodAsync('OnPointerClick', cx, cy, 0);
                } else {
                    // Drag finished outside click threshold → box-select complete.
                    const [cx0, cy0] = toCanvas(Math.min(touchDownX, lastTX), Math.min(touchDownY, lastTY));
                    const [cx1, cy1] = toCanvas(Math.max(touchDownX, lastTX), Math.max(touchDownY, lastTY));
                    dotnetRef.invokeMethodAsync('OnBoxSelectComplete', cx0, cy0, cx1, cy1);
                }
            }, { passive: false });

            canvas.addEventListener('touchcancel', () => {
                if (touchActive) {
                    touchActive = false;
                    if (!touchModeOrbit) dotnetRef.invokeMethodAsync('OnPointerUp');
                }
                pinchActive = false;
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
            const pan = document.getElementById('engine-pan-btn');
            if (pan) pan.remove();
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
