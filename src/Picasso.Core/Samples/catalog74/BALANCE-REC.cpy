      *> BALANCE-REC — the derived output: net balance per person per
      *> group. Has no equivalent in Splitty — Neo4j never has to write
      *> this down, it just recomputes it live on every query. Here,
      *> producing this file IS the batch job.
       01  BALANCE-REC.
           05  GROUP-ID            PIC 9(6).
           05  USER-ID             PIC 9(6).
           05  TOTAL-PAID          PIC 9(7)V99.
           05  TOTAL-OWED          PIC 9(7)V99.
           05  NET-BALANCE         PIC S9(7)V99 SIGN IS TRAILING SEPARATE.
           05  AS-OF-TIMESTAMP     PIC 9(14).
