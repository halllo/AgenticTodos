import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { ChatComponent } from './chat.component';
import { TodosComponent } from './todos.component';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, ChatComponent, TodosComponent],
  template: `
    <div class="app">
      <div class="app__leftPanel" [style.background]="leftPanelBackground()">
        <app-todos />
      </div>

      <div class="app__rightPanel">
        <app-chat (backgroundColorChange)="onBackgroundColorChange($event)" />
      </div>
    </div>
    <router-outlet />
  `,
  styles: `
    .app {
      display: flex;
      height: 100vh;
      min-height: 0;
      min-width: 0;
    }

    .app__leftPanel {
      flex: 1;
      display: flex;
      flex-direction: column;
      align-items: stretch;
      justify-content: stretch;
      transition: background 0.3s ease;
      overflow: hidden;
      min-height: 0;
      min-width: 0;
    }

    .app__rightPanel {
      flex: 1;
      display: flex;
      flex-direction: column;
      align-items: stretch;
      justify-content: stretch;
      overflow: hidden;
      min-height: 0;
      min-width: 0;
    }

    .app__leftPanel app-todos {
      flex: 1;
      min-height: 0;
      min-width: 0;
      display: flex;
    }

    .app__rightPanel app-chat {
      flex: 1;
      min-height: 0;
      min-width: 0;
      display: flex;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {
  protected readonly leftPanelBackground = signal('var(--brand-gradient)');

  protected onBackgroundColorChange(color: string): void {
    this.leftPanelBackground.set(color);
  }
}
