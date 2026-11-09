export function scroll(element, direction) {
    if (!element) return;
    const amount = Math.max(300, element.clientWidth * 0.8);
    element.scrollBy({ left: amount * direction, behavior: "smooth" });
}
