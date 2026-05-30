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
        this.showResultPanel = true;
        this.errorMessage = '';
        this.successMessage = '';
        this.isPassed = false;
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
}
