import { NgModule } from '@angular/core';

import { SharedModule } from '../../shared/shared.module';
import { SnResultSharedModule } from '../sn-result/sn-result-shared.module';
import { OperatorRoutingModule } from './operator-routing.module';
import { OperatorComponent } from './operator.component';

@NgModule({
  declarations: [
    OperatorComponent,
  ],
  imports: [
    SharedModule,
    SnResultSharedModule,
    OperatorRoutingModule,
  ],
})
export class OperatorModule {}
