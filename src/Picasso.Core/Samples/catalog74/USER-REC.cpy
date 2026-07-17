      *> USER-REC — one record per person.
      *> Mirrors Splitty's (:User) node. Deliberately minimal — no
      *> auth/password fields, this demo is about balance derivation,
      *> not reimplementing the whole app.
       01  USER-REC.
           05  USER-ID             PIC 9(6).
           05  USER-NAME           PIC X(30).
           05  USER-EMAIL          PIC X(40).
