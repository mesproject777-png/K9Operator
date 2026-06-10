import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { LoginComponent } from './login/login.component';
import { DashboardComponent } from './dashboard/dashboard.component';
import { authGuard } from './guards/auth.guard';
import { permissionGuard } from './guards/permission.guard';


const routes: Routes = [
  { path: 'login', component: LoginComponent },
  {
    path: 'dashboard',
    component: DashboardComponent,
    canActivate: [authGuard],
    children: [
      { path: '', redirectTo: 'operator', pathMatch: 'full' },
      {
        path: 'operator',
        loadChildren: () => import('./dashboard/operator/operator.module').then((m) => m.OperatorModule),
        canActivate: [permissionGuard],
        data: { pageKey: 'dashboard/operator' }
      },
      { path: 'home', redirectTo: 'operator', pathMatch: 'full' },
      {
        path: 'sn-result',
        loadChildren: () => import('./dashboard/sn-result/sn-result.module').then((m) => m.SnResultModule),
        canActivate: [permissionGuard],
        data: { pageKey: 'dashboard/operator' }
      },
      {
        path: 'profile',
        loadChildren: () => import('./dashboard/profile/profile.module').then((m) => m.ProfileModule),
        canActivate: [permissionGuard],
        data: { pageKey: 'dashboard/profile' }
      },
    ]
  },
  { path: '', redirectTo: 'login', pathMatch: 'full' },
];


@NgModule({
  imports: [RouterModule.forRoot(routes)],
  exports: [RouterModule]
})
export class AppRoutingModule { }
