      *> EXPENSE-REC — one record per expense.
      *> Mirrors Splitty's (:Expense) node; PAYER-ID mirrors the PAID
      *> relationship (Neo4j: (:User)-[:PAID]->(:Expense)).
       01  EXPENSE-REC.
           05  EXPENSE-ID          PIC 9(10).
           05  GROUP-ID            PIC 9(6).
           05  PAYER-ID            PIC 9(6).
           05  EXPENSE-AMOUNT      PIC 9(7)V99.
           05  EXPENSE-DESC        PIC X(30).
           05  EXPENSE-DATE        PIC 9(8).
