import { Component, Input, OnDestroy } from '@angular/core';
import { TranslateService } from '@ngx-translate/core';
import { Subject } from 'rxjs';
import { IObject, IOrganizationUnit, IPerson, ISignature } from 'src/app/core/data/data.classes';
import { RepositoryService } from 'src/app/core/repository.service';
import { DateTools } from 'src/app/core/tools/date.tools';
import { FilesSelector } from 'src/app/core/tools/files.selector';
import { Tools } from 'src/app/core/tools/tools';

class DigitalSignature {

  person: string;
  position: string;
  id: string;
  isValid = false;
  signDate: string;
  role: string;

  constructor(signature: ISignature, person: IPerson, position: IOrganizationUnit) {
    this.id = signature.id;
    const personName = (param1, param2) => `${param1} (${param2})`;
    this.person = personName(person.displayName, position.title);
  }
}

@Component({
    selector: 'app-digital-signatures',
    templateUrl: './digital-signatures.component.html',
    styleUrls: ['./digital-signatures.component.css']
})
/** digital-signatures component*/
export class DigitalSignaturesComponent implements OnDestroy {
  private _document: IObject;
  private ngUnsubscribe = new Subject<void>();

  @Input()
  get document(): IObject {
    return this._document;
  }
  set document(newValue: IObject) {
    this._document = newValue;
    this.loadSignatures(newValue);
  }

  signatures: Array<DigitalSignature>;

  /** digital-signatures ctor */
  constructor(private repository: RepositoryService, private translate: TranslateService) {
    this.signatures = new Array<DigitalSignature>();
  }

  ngOnDestroy(): void {
    this.cancelAllRequests(true);
  }

  private loadSignatures(document: IObject) {
    this.repository.getDocumentSignaturesAsync(document.id, this.ngUnsubscribe)
    .then(signatures => {
        for (const sig of signatures) {
            const sc = this.signatures.find(s => s.id === sig.id);
            if (sc) {
                sc.person = sig.signer;
                sc.isValid = sig.isValid;
                sc.signDate = DateTools.dateToString(sig.signDate, this.translate.currentLang);
                sc.role = sig.role;
            }
        }
    })
    .catch(e => {

    });

    const xpsFile = FilesSelector.getXpsFile(document.actualFileSnapshot.files);
    if (!xpsFile) {
      return;
    }

    for (const signature of xpsFile.signatures) {
      const person = this.repository.getPersonOnOrganizationUnit(signature.positionId);
      const position = this.repository.getOrganizationUnit(signature.positionId);
      const digitalSignature = new DigitalSignature(signature, person, position);
      this.signatures.push(digitalSignature);
    }
  }

  ab2str(buf) {
    return String.fromCharCode.apply(null, new Uint8Array(buf));
  }

  private cancelAllRequests(isCompleted: boolean): void {
    if (this.ngUnsubscribe) {
      // This aborts all HTTP requests.
      this.ngUnsubscribe.next();
      // This completes the subject properly.
      if (isCompleted) {
        this.ngUnsubscribe.complete();
      }
    }
  }
}