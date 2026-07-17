      *> SHARE-REC — one record per share of an expense.
      *> Mirrors Splitty's (:Expense)-[:OWED_BY {amount}]->(:User)
      *> relationship. One EXPENSE-REC can have many SHARE-REC rows.
       01  SHARE-REC.
           05  EXPENSE-ID          PIC 9(10).
           05  GROUP-ID            PIC 9(6).
           05  OWER-ID             PIC 9(6).
           05  SHARE-AMOUNT        PIC 9(7)V99.
