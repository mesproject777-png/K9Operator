import { HttpClient, HttpParams } from '@angular/common/http';
import { Component, OnDestroy } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { environment } from '../../../environments/environment';
import { AuthService, AuthUser } from '../../services/auth.service';

interface OperatorAssemblyRequiredLine {
  id: number;
  son_pn: string;
  son_description: string;
  station_code: string;
  station_name: string;
  item_type: string;
  qty: number;
  bound_qty: number;
  remaining_qty: number;
}

interface OperatorAssemblyBinding {
  id: number;
  workflow_bom_child_id: number;
  child_sn: string;
  child_rsn: string;
  child_pn: string;
  child_status: string;
  created_at: string;
}

interface OperatorAssemblyStatusResponse {
  parent: {
    id: number;
    sn: string;
    rsn: string;
    pn: string;
    station_code: string;
    station_name: string;
  };
  required: OperatorAssemblyRequiredLine[];
  bindings: OperatorAssemblyBinding[];
  requiredTotal: number;
  boundTotal: number;
  remaining: number;
  requiresBinding: boolean;
}

interface OperatorPassResponse {
  message?: string;
  station_code?: string;
  status?: string;
  label_printing?: unknown;
}

type PackagingMode = 'multibox' | 'pallet' | 'shipment';

interface PackagingItem {
  seq?: number;
  id?: number;
  code?: string;
  status?: string;
  item_count?: number;
  added_by?: string;
  added_at?: string;
  sn?: string;
  rsn?: string;
}

interface PackagingStatus {
  enabled: boolean;
  type?: PackagingMode;
  id?: number;
  code?: string;
  target_qty?: number | null;
  scanned_qty?: number;
  remaining_qty?: number | null;
  is_closed?: boolean;
  items?: PackagingItem[];
}

interface PackagingHistoryItem {
  id: number;
  code: string;
  type: 'Multibox' | 'Pallet' | 'Shipment';
  target_qty?: number | null;
  item_count: number;
  status: string;
  created_by?: string;
  created_at?: string;
  closed_at?: string;
}

@Component({
  selector: 'app-operator',
  standalone: false,
  templateUrl: './operator.component.html',
  styleUrl: './operator.component.scss',
})
export class OperatorComponent implements OnDestroy {
  serialNumber = '';
  childSerialNumber = '';
  displayedSerial = '';
  showResultPanel = false;
  isPassing = false;
  isCheckingAssembly = false;
  isBindingChild = false;
  isPassed = false;
  isMultiboxOpen = false;
  isMultiboxLoading = false;
  multiboxSerial = '';
  multiboxStatus: PackagingStatus | null = null;
  activePackagingMode: PackagingMode = 'multibox';
  packagingScanValue = '';
  packagingTargetQty: number | null = null;
  isHistoryOpen = false;
  isHistoryLoading = false;
  packagingHistory: PackagingHistoryItem[] = [];
  successMessage = '';
  errorMessage = '';
  bindingContext: OperatorAssemblyStatusResponse | null = null;
  currentUser: AuthUser | null = null;

  private readonly apiUrl = `${environment.apiUrl}/api/operator`;
  private readonly assemblyStatusApi = `${this.apiUrl}/assembly/status`;
  private readonly assemblyBindApi = `${this.apiUrl}/assembly/bind`;
  private readonly routeSub: Subscription;

  constructor(
    private http: HttpClient,
    private authService: AuthService,
    private route: ActivatedRoute,
    private router: Router
  ) {
    this.currentUser = this.authService.getCurrentUser();
    this.routeSub = this.route.queryParamMap.subscribe((params) => {
      const query = String(params.get('q') || '').trim();
      if (query) {
        this.serialNumber = query;
        this.displayedSerial = query;
        this.errorMessage = '';
        this.successMessage = '';
        this.isPassed = false;
        this.showResultPanel = true;
        this.bindingContext = null;
        this.childSerialNumber = '';
        return;
      }

      this.displayedSerial = '';
      this.showResultPanel = false;
      this.bindingContext = null;
      this.childSerialNumber = '';
    });
  }

  ngOnDestroy(): void {
    this.routeSub.unsubscribe();
  }

  passSerial(): void {
    this.errorMessage = '';
    this.successMessage = '';
    this.isPassed = false;

    if (this.bindingContext) {
      if (this.bindingContext.remaining > 0) {
        this.bindChildSerial();
        return;
      }

      this.completePass(this.bindingContext.parent.sn);
      return;
    }

    const query = this.serialNumber.trim();
    if (!query) {
      this.errorMessage = 'Please enter serial number.';
      return;
    }

    if (!this.currentUser?.login_id) {
      this.errorMessage = 'Station login session is missing. Please login again.';
      return;
    }

    this.checkAssemblyRequirement(query);
  }

  resetBindingSession(): void {
    this.bindingContext = null;
    this.childSerialNumber = '';
    this.displayedSerial = '';
    this.successMessage = '';
    this.errorMessage = '';
  }

  get isOperatorBusy(): boolean {
    return this.isPassing || this.isCheckingAssembly || this.isBindingChild;
  }

  get footerButtonLabel(): string {
    if (this.isOperatorBusy) {
      return this.bindingContext ? 'Checking...' : 'Entering...';
    }

    if (!this.bindingContext) {
      return 'Enter';
    }

    return this.bindingContext.remaining > 0 ? 'Bind' : 'Pass Station';
  }

  get requiredChildSummary(): string {
    if (!this.bindingContext?.required?.length) {
      return '-';
    }

    return this.bindingContext.required
      .map((line) => `${line.son_pn} ${line.bound_qty}/${line.qty}`)
      .join(', ');
  }

  get nextRequiredChildPn(): string {
    const pending = this.bindingContext?.required?.find((line) => line.remaining_qty > 0);
    return pending?.son_pn || 'child SN';
  }

  private checkAssemblyRequirement(query: string): void {
    if (!this.currentUser?.login_id) {
      this.errorMessage = 'Station login session is missing. Please login again.';
      return;
    }

    this.isCheckingAssembly = true;
    let params = new HttpParams()
      .set('parent_query', query)
      .set('loginId', this.currentUser.login_id);
    if (this.currentUser.workflow_part_id) {
      params = params.set('workflowPartId', String(this.currentUser.workflow_part_id));
    }

    this.http.get<OperatorAssemblyStatusResponse>(this.assemblyStatusApi, { params }).subscribe({
      next: (status) => {
        this.isCheckingAssembly = false;

        if (status.requiresBinding && status.remaining > 0) {
          this.bindingContext = status;
          this.displayedSerial = status.parent.sn;
          this.childSerialNumber = '';
          this.successMessage = `Parent set: ${status.parent.sn}. Required: ${this.requiredChildSummary}`;
          return;
        }

        this.completePass(query);
      },
      error: (error) => {
        this.isCheckingAssembly = false;
        this.errorMessage = error?.error?.message || error?.error?.error || 'Unable to check BOM binding requirement.';
      },
    });
  }

  private bindChildSerial(): void {
    const childQuery = this.childSerialNumber.trim();
    if (!childQuery) {
      this.errorMessage = 'Please enter child serial number.';
      return;
    }

    if (!this.bindingContext || !this.currentUser?.login_id) {
      this.errorMessage = 'Parent serial session is missing. Please scan parent serial again.';
      return;
    }

    this.isBindingChild = true;
    this.http.post<OperatorAssemblyStatusResponse>(this.assemblyBindApi, {
      parent_query: this.bindingContext.parent.sn,
      child_query: childQuery,
      loginId: this.currentUser.login_id,
      workflowPartId: this.currentUser.workflow_part_id,
    }).subscribe({
      next: (status) => {
        this.isBindingChild = false;
        this.bindingContext = status;
        this.childSerialNumber = '';
        this.successMessage = status.remaining > 0
          ? `Child bound. Remaining: ${status.remaining}. Required: ${this.requiredChildSummary}`
          : 'All required child serials bound. Passing station...';

        if (status.remaining === 0) {
          this.completePass(status.parent.sn);
        }
      },
      error: (error) => {
        this.isBindingChild = false;
        this.errorMessage = error?.error?.message || error?.error?.error || 'Unable to bind child serial.';
      },
    });
  }

  private completePass(query: string): void {
    if (!this.currentUser?.login_id) {
      this.errorMessage = 'Station login session is missing. Please login again.';
      return;
    }

    this.isPassing = true;
    this.http.post<OperatorPassResponse>(`${this.apiUrl}/pass`, {
      query,
      loginId: this.currentUser.login_id,
      workflowPartId: this.currentUser.workflow_part_id,
    }).subscribe({
      next: (response) => {
        this.isPassing = false;
        this.isPassed = true;
        this.displayedSerial = '';
        this.showResultPanel = false;
        this.bindingContext = null;
        this.childSerialNumber = '';
        this.serialNumber = '';
        this.successMessage = response?.message || 'Station passed successfully.';
        this.serialNumber = '';
        this.router.navigate(['/dashboard/operator']);
      },
      error: (error) => {
        this.isPassing = false;
        const assembly = error?.error?.assembly as OperatorAssemblyStatusResponse | undefined;
        if (assembly?.requiresBinding) {
          this.bindingContext = assembly;
          this.displayedSerial = assembly.parent.sn;
        }

        this.errorMessage = error?.error?.message || error?.error?.error || 'Unable to pass station.';
      },
    });
  }

  canUseMultibox(): boolean {
    return Boolean(this.currentUser?.is_pack_station && Number(this.currentUser?.box_qty || 0) > 0);
  }

  toggleMultibox(): void {
    this.openPackagingMode('multibox');
  }

  openPackagingMode(mode: PackagingMode): void {
    this.errorMessage = '';
    this.successMessage = '';
    this.activePackagingMode = mode;
    this.isMultiboxOpen = true;
    this.packagingScanValue = '';
    this.loadPackagingStatus();
  }

  loadMultiboxStatus(): void {
    this.loadPackagingStatus();
  }

  loadPackagingStatus(): void {
    if (!this.currentUser?.login_id) {
      return;
    }

    this.isMultiboxLoading = true;
    const endpoint = this.activePackagingMode === 'multibox'
      ? `${this.apiUrl}/multibox/status`
      : `${this.apiUrl}/${this.activePackagingMode}/status`;
    this.http.get<any>(endpoint, {
      params: { loginId: this.currentUser.login_id }
    }).subscribe({
      next: (status) => {
        this.multiboxStatus = this.normalizePackagingStatus(status);
        this.packagingTargetQty = this.multiboxStatus?.target_qty ?? null;
        this.isMultiboxLoading = false;
      },
      error: (error) => {
        this.errorMessage = error?.error?.message || `Unable to load ${this.activePackagingLabel.toLowerCase()} status.`;
        this.isMultiboxLoading = false;
      }
    });
  }

  submitSerial(): void {
    if (this.canUseMultibox()) {
      this.scanMultiboxSerial();
      return;
    }

    this.passSerial();
  }

  scanMultiboxSerial(value?: string): void {
    this.errorMessage = '';
    this.successMessage = '';

    const query = String(value ?? this.currentScanValue).trim();
    if (!query) {
      this.errorMessage = `Please enter ${this.scanLabel.toLowerCase()}.`;
      return;
    }

    if (!this.currentUser?.login_id) {
      this.errorMessage = 'Station login session is missing. Please login again.';
      return;
    }

    if (this.activePackagingMode !== 'multibox' && (!this.packagingTargetQty || this.packagingTargetQty <= 0)) {
      this.errorMessage = `Please enter ${this.activePackagingLabel.toLowerCase()} quantity.`;
      return;
    }

    this.isMultiboxLoading = true;
    const endpoint = this.activePackagingMode === 'multibox'
      ? `${this.apiUrl}/multibox/scan`
      : `${this.apiUrl}/${this.activePackagingMode}/scan`;
    const body: Record<string, unknown> = {
      loginId: this.currentUser.login_id,
      query
    };
    if (this.activePackagingMode !== 'multibox') {
      body['targetQty'] = this.packagingTargetQty;
    }

    this.http.post<any>(endpoint, body).subscribe({
      next: (response) => {
        this.multiboxStatus = this.normalizePackagingStatus(response);
        this.packagingTargetQty = this.multiboxStatus.target_qty ?? this.packagingTargetQty;
        this.multiboxSerial = '';
        this.packagingScanValue = '';
        this.serialNumber = '';
        this.displayedSerial = query;
        this.showResultPanel = false;
        this.isPassed = true;
        this.successMessage = response?.message || `${this.scanLabel} added.`;
        this.isMultiboxLoading = false;
        if (this.isHistoryOpen) {
          this.loadPackagingHistory();
        }
      },
      error: (error) => {
        this.errorMessage = error?.error?.message || `Unable to scan ${this.scanLabel.toLowerCase()}.`;
        this.isMultiboxLoading = false;
      }
    });
  }

  get multiboxItems(): Array<any> {
    return this.multiboxStatus?.items || [];
  }

  get multiboxProgressText(): string {
    const total = this.activePackagingMode === 'multibox'
      ? (this.multiboxStatus?.target_qty || this.currentUser?.box_qty || 0)
      : (this.packagingTargetQty || this.multiboxStatus?.target_qty || 0);
    return `${this.multiboxStatus?.scanned_qty || 0} / ${total}`;
  }

  get activePackagingLabel(): string {
    if (this.activePackagingMode === 'pallet') {
      return 'Pallet';
    }

    if (this.activePackagingMode === 'shipment') {
      return 'Shipment';
    }

    return 'Multibox';
  }

  get activePackagingCode(): string {
    return this.multiboxStatus?.code || 'Generating...';
  }

  get activePackagingIcon(): string {
    if (this.activePackagingMode === 'pallet') {
      return 'pallet';
    }

    if (this.activePackagingMode === 'shipment') {
      return 'local_shipping';
    }

    return 'inventory_2';
  }

  get scanLabel(): string {
    if (this.activePackagingMode === 'pallet') {
      return 'Multibox Number';
    }

    if (this.activePackagingMode === 'shipment') {
      return 'Pallet Number';
    }

    return 'Product Serial';
  }

  get currentScanValue(): string {
    return this.activePackagingMode === 'multibox' ? this.serialNumber : this.packagingScanValue;
  }

  set currentScanValue(value: string) {
    if (this.activePackagingMode === 'multibox') {
      this.serialNumber = value;
      return;
    }

    this.packagingScanValue = value;
  }

  get isActivePackagingClosed(): boolean {
    return !!this.multiboxStatus?.is_closed;
  }

  get remainingText(): string {
    const remaining = this.multiboxStatus?.remaining_qty;
    return remaining === null || remaining === undefined ? '-' : String(remaining);
  }

  showHistory(): void {
    this.isHistoryOpen = true;
    this.loadPackagingHistory();
  }

  closeHistory(): void {
    this.isHistoryOpen = false;
  }

  loadPackagingHistory(): void {
    if (!this.currentUser?.login_id) {
      return;
    }

    this.isHistoryLoading = true;
    this.http.get<{ data: PackagingHistoryItem[] }>(`${this.apiUrl}/packaging/history`, {
      params: { loginId: this.currentUser.login_id }
    }).subscribe({
      next: (response) => {
        this.packagingHistory = response.data || [];
        this.isHistoryLoading = false;
      },
      error: (error) => {
        this.errorMessage = error?.error?.message || 'Unable to load packaging history.';
        this.isHistoryLoading = false;
      }
    });
  }

  private normalizePackagingStatus(status: any): PackagingStatus {
    return {
      enabled: status?.enabled !== false,
      type: this.activePackagingMode,
      id: status?.id,
      code: status?.code || status?.box_no || status?.pallet_no || status?.shipment_no,
      target_qty: status?.target_qty ?? status?.box_qty ?? null,
      scanned_qty: status?.scanned_qty ?? 0,
      remaining_qty: status?.remaining_qty ?? null,
      is_closed: !!status?.is_closed,
      items: status?.items || []
    };
  }
}
