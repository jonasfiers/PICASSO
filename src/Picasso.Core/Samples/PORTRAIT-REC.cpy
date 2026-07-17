      *> PORTRAIT-REC — synthetic demo record, not part of CATALOG-74.
      *> Exercises what none of the real copybooks do in one place:
      *> a 3-level nested group, COMP-3 packed decimal, and a
      *> SIGN IS LEADING SEPARATE numeric (CATALOG-74's BALANCE-REC
      *> uses TRAILING SEPARATE instead, so together the two corpora
      *> cover both sign placements).
       01  PORTRAIT-REC.
           05  ARTIST-ID        PIC 9(6).
           05  ARTIST-NAME      PIC X(30).
           05  STUDIO-ADDRESS.
               10  STREET       PIC X(25).
               10  CITY         PIC X(20).
               10  POSTAL-CODE  PIC X(10).
           05  CANVAS-COUNT     PIC 9(4).
           05  TOTAL-VALUE      PIC S9(9)V99 COMP-3.
           05  NET-WORTH        PIC S9(9)V99 SIGN IS LEADING SEPARATE.
