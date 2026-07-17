*> AMOUNT-OWED-REC — intermediate output of CALC-OWED: total owed
*> per person per group. Consumed by MERGE-BALANCE. Fields are
*> prefixed (AO-) since programs that merge this against
*> AMOUNT-PAID-REC need both loaded at once without ambiguity.
01  AMOUNT-OWED-REC.
    05  AO-GROUP-ID         PIC 9(6).
    05  AO-USER-ID          PIC 9(6).
    05  AO-TOTAL-OWED       PIC 9(7)V99.
