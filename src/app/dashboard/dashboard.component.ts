import { HttpClient, HttpParams } from '@angular/common/http';
import { Component, OnDestroy } from '@angular/core';
import { Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { environment } from '../../environments/environment';
import { AuthService, AuthUser } from '../services/auth.service';

type OperatorLabelPrinterConfig = {
  isLabelPrintingEnabled: boolean;
  stationCode?: string;
  stationName?: string;
  workflowPartId?: number;
  labelCode?: string;
  labelDescription?: string;
  printerId?: string;
  printerName?: string;
  ipAddress?: string;
  port?: string;
  status?: string;
  message?: string;
  success?: boolean;
};

type OperatorWeighingConfig = {
  isWeighingEnabled: boolean;
  stationCode?: string;
  stationName?: string;
  workflowPartId?: number;
  minimumWeight?: string;
  maximumWeight?: string;
  tolerance?: string;
};

@Component({
  selector: 'app-dashboard',
  standalone: false,
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss'
})
export class DashboardComponent implements OnDestroy {
  currentUser: AuthUser | null = null;
  isProfileMenuOpen = false;
  headerSearch = '';
  labelPrinterConfig: OperatorLabelPrinterConfig | null = null;
  weighingConfig: OperatorWeighingConfig | null = null;
  labelPrinterIp = '';
  labelPrinterMessage = '';
  labelPrinterMessageType: 'success' | 'error' | 'info' = 'info';
  isLabelPrinterLoading = false;
  isLabelPrinterSaving = false;
  isTestingPrinterConnection = false;
  isSendingTestPrint = false;

  private readonly operatorApiUrl = `${environment.apiUrl}/api/operator`;
  private readonly authSub: Subscription;
  private labelPrinterRefreshId: ReturnType<typeof setInterval> | null = null;

  constructor(
    private http: HttpClient,
    private authService: AuthService,
    private router: Router
  ) {
    this.currentUser = this.authService.getCurrentUser();
    this.loadLabelPrinterConfig();
    this.loadWeighingConfig();
    this.startLabelPrinterRefresh();

    this.authSub = this.authService.currentUser$.subscribe((user) => {
      this.currentUser = user;
      this.labelPrinterConfig = null;
      this.weighingConfig = null;
      this.labelPrinterIp = '';
      this.labelPrinterMessage = '';
      this.loadLabelPrinterConfig();
      this.loadWeighingConfig();
      this.startLabelPrinterRefresh();
    });
  }

  ngOnDestroy(): void {
    this.authSub.unsubscribe();
    this.stopLabelPrinterRefresh();
  }

  toggleProfileMenu(): void {
    this.isProfileMenuOpen = !this.isProfileMenuOpen;
  }

  openProfile(): void {
    this.isProfileMenuOpen = false;
    this.router.navigate(['/dashboard/profile']);
  }

  openSettings(): void {
    this.isProfileMenuOpen = false;
    this.router.navigate(['/dashboard/profile'], { queryParams: { section: 'settings' } });
  }

  logout(): void {
    this.isProfileMenuOpen = false;
    this.authService.logout();
  }

  searchSerialNumber(event?: Event): void {
    event?.preventDefault();

    const serialNumber = this.headerSearch.trim();
    if (!serialNumber) {
      return;
    }

    this.router.navigate(['/dashboard/operator'], {
      queryParams: { q: serialNumber, t: Date.now() },
    });
  }

  onHeaderSearchInput(): void {
    if (!this.headerSearch.trim()) {
      return;
    }

    this.searchSerialNumber();
  }

  get shouldShowLabelPrinterControls(): boolean {
    const currentUrl = this.router.parseUrl(this.router.url);
    const path = currentUrl.root.children['primary']?.segments.map((segment) => segment.path).join('/') || '';
    return path === 'dashboard/operator';
  }

  get shouldShowWeighingControls(): boolean {
    return this.shouldShowLabelPrinterControls &&
      Boolean(this.weighingConfig?.isWeighingEnabled) &&
      !this.shouldShowPrinterControls;
  }

  get shouldShowPrinterControls(): boolean {
    return this.shouldShowLabelPrinterControls &&
      Boolean(this.labelPrinterConfig?.isLabelPrintingEnabled);
  }

  loadLabelPrinterConfig(silent = false): void {
    const loginId = this.currentUser?.login_id;
    if (!loginId) {
      this.labelPrinterConfig = null;
      this.labelPrinterIp = '';
      this.stopLabelPrinterRefresh();
      return;
    }

    if (!silent) {
      this.isLabelPrinterLoading = true;
    }

    const params = this.buildOperatorStationParams(loginId);
    this.http.get<OperatorLabelPrinterConfig>(`${this.operatorApiUrl}/label-printing-config`, { params }).subscribe({
      next: (config) => {
        this.isLabelPrinterLoading = false;
        if (!config?.isLabelPrintingEnabled) {
          this.labelPrinterConfig = null;
          this.labelPrinterIp = '';
          return;
        }

        this.labelPrinterConfig = config;
        if (!this.isLabelPrinterSaving && !this.isTestingPrinterConnection && !this.isSendingTestPrint) {
          this.labelPrinterIp = config.ipAddress || '';
        }
      },
      error: () => {
        this.isLabelPrinterLoading = false;
        if (!silent) {
          this.labelPrinterConfig = null;
          this.labelPrinterIp = '';
        }
      },
    });
  }

  loadWeighingConfig(): void {
    const loginId = this.currentUser?.login_id;
    if (!loginId) {
      this.weighingConfig = null;
      return;
    }

    const params = this.buildOperatorStationParams(loginId);
    this.http.get<OperatorWeighingConfig>(`${this.operatorApiUrl}/weighing-config`, { params }).subscribe({
      next: (config) => {
        this.weighingConfig = config?.isWeighingEnabled ? config : null;
      },
      error: () => {
        this.weighingConfig = null;
      },
    });
  }

  saveLabelPrinterIp(): void {
    const loginId = this.currentUser?.login_id;
    const printerIp = this.labelPrinterIp.trim();
    if (!loginId || !this.labelPrinterConfig?.isLabelPrintingEnabled) {
      return;
    }

    if (!printerIp) {
      this.setLabelPrinterMessage('Printer IP required', 'error');
      return;
    }

    this.isLabelPrinterSaving = true;
    this.http.put<OperatorLabelPrinterConfig>(`${this.operatorApiUrl}/label-printing-config`, {
      loginId,
      workflowPartId: this.currentUser?.workflow_part_id,
      stationCode: this.currentUser?.station_code,
      ipAddress: printerIp,
      port: this.labelPrinterConfig.port || '9100',
    }).subscribe({
      next: (config) => {
        this.isLabelPrinterSaving = false;
        this.applyLabelPrinterActionResponse(config, 'Saved');
      },
      error: (error) => {
        this.isLabelPrinterSaving = false;
        this.setLabelPrinterMessage(error?.error?.message || error?.error?.error || 'Save failed', 'error');
      },
    });
  }

  testLabelPrinterConnection(): void {
    const loginId = this.currentUser?.login_id;
    const printerIp = this.labelPrinterIp.trim();
    if (!loginId || !this.labelPrinterConfig?.isLabelPrintingEnabled) {
      return;
    }

    if (!printerIp) {
      this.setLabelPrinterMessage('Printer IP required', 'error');
      return;
    }

    this.isTestingPrinterConnection = true;
    this.http.post<OperatorLabelPrinterConfig>(`${this.operatorApiUrl}/label-printing-config/test-connection`, {
      loginId,
      workflowPartId: this.currentUser?.workflow_part_id,
      stationCode: this.currentUser?.station_code,
      ipAddress: printerIp,
      port: this.labelPrinterConfig.port || '9100',
    }).subscribe({
      next: (config) => {
        this.isTestingPrinterConnection = false;
        this.applyLabelPrinterActionResponse(config, config.success === false ? 'Connection failed' : 'Connected');
      },
      error: (error) => {
        this.isTestingPrinterConnection = false;
        this.setLabelPrinterMessage(error?.error?.message || error?.error?.error || 'Connection failed', 'error');
      },
    });
  }

  sendLabelPrinterTestPrint(): void {
    const loginId = this.currentUser?.login_id;
    const printerIp = this.labelPrinterIp.trim();
    if (!loginId || !this.labelPrinterConfig?.isLabelPrintingEnabled) {
      return;
    }

    if (!printerIp) {
      this.setLabelPrinterMessage('Printer IP required', 'error');
      return;
    }

    this.isSendingTestPrint = true;
    this.http.post<OperatorLabelPrinterConfig>(`${this.operatorApiUrl}/label-printing-config/test-print`, {
      loginId,
      workflowPartId: this.currentUser?.workflow_part_id,
      stationCode: this.currentUser?.station_code,
      ipAddress: printerIp,
      port: this.labelPrinterConfig.port || '9100',
    }).subscribe({
      next: (config) => {
        this.isSendingTestPrint = false;
        this.applyLabelPrinterActionResponse(config, config.success === false ? 'Test failed' : 'Test sent');
      },
      error: (error) => {
        this.isSendingTestPrint = false;
        this.setLabelPrinterMessage(error?.error?.message || error?.error?.error || 'Test failed', 'error');
      },
    });
  }

  private applyLabelPrinterActionResponse(config: OperatorLabelPrinterConfig, fallbackMessage: string): void {
    if (config?.isLabelPrintingEnabled) {
      this.labelPrinterConfig = config;
      this.labelPrinterIp = config.ipAddress || this.labelPrinterIp;
    }

    this.setLabelPrinterMessage(config?.message || fallbackMessage, config?.success === false ? 'error' : 'success');
  }

  private setLabelPrinterMessage(message: string, type: 'success' | 'error' | 'info'): void {
    this.labelPrinterMessage = message;
    this.labelPrinterMessageType = type;
  }

  private buildOperatorStationParams(loginId: string): HttpParams {
    let params = new HttpParams().set('loginId', loginId);
    if (this.currentUser?.workflow_part_id) {
      params = params.set('workflowPartId', String(this.currentUser.workflow_part_id));
    }
    if (this.currentUser?.station_code) {
      params = params.set('stationCode', this.currentUser.station_code);
    }

    return params;
  }

  private startLabelPrinterRefresh(): void {
    this.stopLabelPrinterRefresh();
    if (!this.currentUser?.login_id) {
      return;
    }

    this.labelPrinterRefreshId = setInterval(() => {
      this.loadLabelPrinterConfig(true);
      this.loadWeighingConfig();
    }, 15000);
  }

  private stopLabelPrinterRefresh(): void {
    if (this.labelPrinterRefreshId) {
      clearInterval(this.labelPrinterRefreshId);
      this.labelPrinterRefreshId = null;
    }
  }
}
