      *> USER-AUTH-REC — credential store, deliberately kept separate
      *> from USER-REC. Real mainframe security data (RACF) lives in
      *> its own dataset, segregated from business data — same idea
      *> here. PASSWORD-HASH is written/checked by api-cobol (bcrypt),
      *> not by COBOL — GnuCOBOL has no hashing primitives of its own.
       01  USER-AUTH-REC.
           05  USER-ID             PIC 9(6).
           05  PASSWORD-HASH       PIC X(60).
