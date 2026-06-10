import { NgModule } from '@angular/core';

import { SnResultRoutingModule } from './sn-result-routing.module';
import { SnResultSharedModule } from './sn-result-shared.module';

@NgModule({
  imports: [
    SnResultSharedModule,
    SnResultRoutingModule,
  ],
})
export class SnResultModule {}
