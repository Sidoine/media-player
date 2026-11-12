const handlers = new WeakMap();
const idleDelay = 2500;

export function attach(container) {
    const show = () => {
        container.classList.remove("player-controls-hidden");
        const state = handlers.get(container);
        clearTimeout(state?.timer);
        handlers.set(container, { ...state, timer: setTimeout(() => container.classList.add("player-controls-hidden"), idleDelay) });
    };

    handlers.set(container, { show, timer: null });
    container.addEventListener("pointermove", show);
    container.addEventListener("pointerdown", show);
    container.addEventListener("keydown", show);
    container.addEventListener("focusin", show);
    show();
}

export function detach(container) {
    const state = handlers.get(container);
    if (!state) return;
    clearTimeout(state.timer);
    container.classList.remove("player-controls-hidden");
    container.removeEventListener("pointermove", state.show);
    container.removeEventListener("pointerdown", state.show);
    container.removeEventListener("keydown", state.show);
    container.removeEventListener("focusin", state.show);
    handlers.delete(container);
}
