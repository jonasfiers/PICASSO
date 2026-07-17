*> AMOUNT-PAID-REC — intermediate output of CALC-PAID: total paid
*> per person per group. Consumed by MERGE-BALANCE. Fields are
*> prefixed (AP-) for the same reason as AMOUNT-OWED-REC's AO- prefix.
01  AMOUNT-PAID-REC.
    05  AP-GROUP-ID         PIC 9(6).
    05  AP-USER-ID          PIC 9(6).
    05  AP-TOTAL-PAID       PIC 9(7)V99.
