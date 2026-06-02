import { HttpClient } from '@angular/common/http';
import { Component, OnDestroy } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { environment } from '../../../environments/environment';
import { AuthService, AuthUser } from '../../services/auth.service';

@Component({
  selector: 'app-operator',
  standalone: false,
  templateUrl: './operator.component.html',
  styleUrl: './operator.component.scss',
})
export class OperatorComponent implements OnDestroy {
  serialNumber = '';
  displayedSerial = '';
  showResultPanel = false;
  isPassing = false;
  isPassed = false;
  isMultiboxOpen = false;
  isMultiboxLoading = false;
  multiboxSerial = '';
  multiboxStatus: {
    enabled: boolean;
    box_qty?: number;
    scanned_qty?: number;
    remaining_qty?: number;
    box_no?: string;
    is_closed?: boolean;
    items?: Array<{
      seq?: number;
      sn?: string;
      rsn?: string;
      status?: string;
      added_by?: string;
      added_at?: string;
    }>;
  } | null = null;
  successMessage = '';
  errorMessage = '';
  currentUser: AuthUser | null = null;

  private readonly apiUrl = `${environment.apiUrl}/api/operator`;
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
        return;
      }

      this.displayedSerial = '';
      this.showResultPanel = false;
    });
  }

  ngOnDestroy(): void {
    this.routeSub.unsubscribe();
  }

  passSerial(): void {
    this.errorMessage = '';
    this.successMessage = '';
    this.isPassed = false;

    const query = this.serialNumber.trim();
    if (!query) {
      this.errorMessage = 'Please enter serial number.';
      return;
    }

    if (!this.currentUser?.login_id) {
      this.errorMessage = 'Station login session is missing. Please login again.';
      return;
    }

    this.isPassing = true;
    this.http.post<{ message?: string }>(`${this.apiUrl}/pass`, {
      query,
      loginId: this.currentUser.login_id,
    }).subscribe({
      next: (response) => {
        this.isPassing = false;
        this.isPassed = true;
        this.displayedSerial = '';
        this.showResultPanel = false;
        this.successMessage = response?.message || 'Station passed successfully.';
        this.router.navigate(['/dashboard/operator']);
      },
      error: (error) => {
        this.isPassing = false;
        this.errorMessage = error?.error?.message || error?.error?.error || 'Unable to pass station.';
      },
    });
  }

  canUseMultibox(): boolean {
    return Boolean(this.currentUser?.is_pack_station && Number(this.currentUser?.box_qty || 0) > 0);
  }

  toggleMultibox(): void {
    this.errorMessage = '';
    this.successMessage = '';
    this.isMultiboxOpen = true;
    this.loadMultiboxStatus();
  }

  loadMultiboxStatus(): void {
    if (!this.currentUser?.login_id) {
      return;
    }

    this.isMultiboxLoading = true;
    this.http.get<any>(`${this.apiUrl}/multibox/status`, {
      params: { loginId: this.currentUser.login_id }
    }).subscribe({
      next: (status) => {
        this.multiboxStatus = status;
        this.isMultiboxLoading = false;
      },
      error: (error) => {
        this.errorMessage = error?.error?.message || 'Unable to load multibox status.';
        this.isMultiboxLoading = false;
      }
    });
  }

  submitSerial(): void {
    if (this.canUseMultibox()) {
      this.scanMultiboxSerial(this.serialNumber);
      return;
    }

    this.passSerial();
  }

  scanMultiboxSerial(value?: string): void {
    this.errorMessage = '';
    this.successMessage = '';

    const query = String(value ?? this.multiboxSerial).trim();
    if (!query) {
      this.errorMessage = 'Please enter serial number.';
      return;
    }

    if (!this.currentUser?.login_id) {
      this.errorMessage = 'Station login session is missing. Please login again.';
      return;
    }

    this.isMultiboxLoading = true;
    this.http.post<any>(`${this.apiUrl}/multibox/scan`, {
      loginId: this.currentUser.login_id,
      query
    }).subscribe({
      next: (response) => {
        this.multiboxStatus = {
          enabled: true,
          box_qty: response.box_qty,
          scanned_qty: response.scanned_qty,
          remaining_qty: response.remaining_qty,
          box_no: response.box_no,
          is_closed: response.is_closed,
          items: response.items || []
        };
        this.multiboxSerial = '';
        this.serialNumber = '';
        this.displayedSerial = query;
        this.showResultPanel = false;
        this.isPassed = true;
        this.successMessage = response?.message || 'Serial added to box.';
        this.isMultiboxLoading = false;
        this.router.navigate(['/dashboard/operator']);
      },
      error: (error) => {
        this.errorMessage = error?.error?.message || 'Unable to scan serial into box.';
        this.isMultiboxLoading = false;
      }
    });
  }

  get multiboxItems(): Array<any> {
    return this.multiboxStatus?.items || [];
  }

  get multiboxProgressText(): string {
    return `${this.multiboxStatus?.scanned_qty || 0} / ${this.multiboxStatus?.box_qty || this.currentUser?.box_qty || 0}`;
  }
}
