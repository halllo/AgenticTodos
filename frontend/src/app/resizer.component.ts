import { ChangeDetectionStrategy, Component, input, signal } from '@angular/core';

/**
 * A draggable vertical divider placed between two horizontally-arranged panels.
 *
 * It owns the resize behaviour: given references to the two resizable elements,
 * dragging the handle resizes them directly. The left panel becomes a fixed
 * width (flex-basis) and the right panel flexes to fill the remaining space.
 * Neither panel is allowed to shrink below `minPanelWidth`.
 *
 * Pointer capture is used so dragging keeps working even when the cursor moves
 * over iframes or leaves the handle.
 */
@Component({
  selector: 'app-resizer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div
      class="resizer"
      [class.resizer--active]="dragging()"
      role="separator"
      aria-orientation="vertical"
      aria-label="Resize panels"
      (pointerdown)="onPointerDown($event)"
      (pointermove)="onPointerMove($event)"
      (pointerup)="onPointerUp($event)"
      (pointercancel)="onPointerUp($event)"
    >
      <div class="resizer__grip"></div>
    </div>
  `,
  styles: `
    :host {
      flex: 0 0 8px;
      align-self: stretch;
      display: block;
    }

    .resizer {
      width: 100%;
      height: 100%;
      display: flex;
      align-items: center;
      justify-content: center;
      cursor: col-resize;
      background: var(--border);
      user-select: none;
      touch-action: none;
      transition: background 0.15s ease;
    }

    .resizer:hover,
    .resizer--active {
      background: var(--brand-primary);
    }

    .resizer__grip {
      width: 2px;
      height: 36px;
      border-radius: 2px;
      background: rgba(0, 0, 0, 0.2);
      pointer-events: none;
      transition: background 0.15s ease;
    }

    .resizer:hover .resizer__grip,
    .resizer--active .resizer__grip {
      background: rgba(255, 255, 255, 0.8);
    }
  `,
})
export class ResizerComponent {
  /** The two panels this divider sits between and resizes. */
  readonly leftPanel = input.required<HTMLElement>();
  readonly rightPanel = input.required<HTMLElement>();
  /** Minimum width (px) either panel may shrink to while resizing. */
  readonly minPanelWidth = input(240);

  protected readonly dragging = signal(false);

  // Captured at drag start so the move math is drift-free.
  private startX = 0;
  private startLeftWidth = 0;
  private maxLeftWidth = 0;

  protected onPointerDown(event: PointerEvent): void {
    event.preventDefault();
    this.startX = event.clientX;
    this.startLeftWidth = this.leftPanel().offsetWidth;
    // The space the two panels share stays constant during a single drag.
    this.maxLeftWidth =
      this.leftPanel().offsetWidth + this.rightPanel().offsetWidth - this.minPanelWidth();
    this.dragging.set(true);
    (event.target as HTMLElement).setPointerCapture(event.pointerId);
  }

  protected onPointerMove(event: PointerEvent): void {
    if (!this.dragging()) {
      return;
    }
    const delta = event.clientX - this.startX;
    const width = Math.min(
      this.maxLeftWidth,
      Math.max(this.minPanelWidth(), this.startLeftWidth + delta),
    );
    // Left panel takes a fixed width; the right panel's flex:1 fills the rest.
    this.leftPanel().style.flex = `0 0 ${width}px`;
  }

  protected onPointerUp(event: PointerEvent): void {
    if (!this.dragging()) {
      return;
    }
    this.dragging.set(false);
    (event.target as HTMLElement).releasePointerCapture(event.pointerId);
  }
}
