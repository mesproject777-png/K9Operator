import { Component } from '@angular/core';
import { AuthService } from '../services/auth.service';

interface SidebarItem {
  label: string;
  route: string;
  icon: string;
  pageKey: string;
}

@Component({
  selector: 'app-sidebar',
  standalone: false,
  templateUrl: './sidebar.component.html',
  styleUrl: './sidebar.component.scss'
})
export class SidebarComponent {
  readonly sidebarItems: SidebarItem[] = [
    { label: 'Operator', route: '/dashboard/operator', icon: 'precision_manufacturing', pageKey: 'dashboard/operator' },
  ];

  constructor(private authService: AuthService) {}

  canAccess(pageKey: string): boolean {
    return this.authService.hasAccess(pageKey);
  }

}
