import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { ChatComponent } from './chat.component';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, ChatComponent],
  template: `
    <div class="app-layout">
      <div class="left-panel" [style.background]="leftPanelBackground()">
        <h1>Agentic Todos</h1>
      </div>
      
      <app-chat (backgroundColorChange)="onBackgroundColorChange($event)" />
    </div>
    <router-outlet />
  `,
  styles: `
    .app-layout {
      display: flex;
      height: 100vh;
    }

    .left-panel {
      flex: 1;
      display: flex;
      align-items: center;
      justify-content: center;
      transition: background 0.3s ease;
      
      h1 {
        font-size: 3rem;
        font-weight: 700;
        color: white;
        text-align: center;
        margin: 0;
      }
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {
  protected readonly leftPanelBackground = signal('linear-gradient(135deg, #667eea 0%, #764ba2 100%)');

  protected onBackgroundColorChange(color: string): void {
    this.leftPanelBackground.set(color);
  }
}
