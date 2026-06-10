import { NgModule } from '@angular/core';

import { SharedModule } from '../../shared/shared.module';
import { SnResultComponent } from './sn-result.component';

@NgModule({
  declarations: [
    SnResultComponent,
  ],
  imports: [
    SharedModule,
  ],
  exports: [
    SnResultComponent,
  ],
})
export class SnResultSharedModule {}
