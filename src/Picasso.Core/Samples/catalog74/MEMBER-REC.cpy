      *> MEMBER-REC — one record per (group, user) pair.
      *> Mirrors Splitty's (:User)-[:MEMBER_OF]->(:Group) relationship.
      *> Exists independently of expenses — someone can be a group
      *> member with a zero balance and no records in SHARE-REC at all.
       01  MEMBER-REC.
           05  GROUP-ID            PIC 9(6).
           05  USER-ID             PIC 9(6).
