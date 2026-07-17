// Field specs mirror the copybooks byte-for-byte -- see
// cobol/copybooks/*.cpy for the authoritative PIC clauses. Offsets
// here are 0-indexed; the copybooks are documented 1-indexed (COBOL
// convention), so every start value here is (copybook column - 1).

const USER_SPEC = [
  { name: 'id', start: 0, len: 6, type: 'int' },
  { name: 'name', start: 6, len: 30, type: 'str' },
  { name: 'email', start: 36, len: 40, type: 'str' },
]

const USER_AUTH_SPEC = [
  { name: 'id', start: 0, len: 6, type: 'int' },
  { name: 'hash', start: 6, len: 60, type: 'str' },
]

const GROUP_SPEC = [
  { name: 'id', start: 0, len: 6, type: 'int' },
  { name: 'name', start: 6, len: 30, type: 'str' },
]

const MEMBER_SPEC = [
  { name: 'groupId', start: 0, len: 6, type: 'int' },
  { name: 'userId', start: 6, len: 6, type: 'int' },
]

const EXPENSE_SPEC = [
  { name: 'expenseId', start: 0, len: 10, type: 'int' },
  { name: 'groupId', start: 10, len: 6, type: 'int' },
  { name: 'payerId', start: 16, len: 6, type: 'int' },
  { name: 'amount', start: 22, len: 9, type: 'int' },
  { name: 'description', start: 31, len: 30, type: 'str' },
  { name: 'date', start: 61, len: 8, type: 'int' },
]

const SHARE_SPEC = [
  { name: 'expenseId', start: 0, len: 10, type: 'int' },
  { name: 'groupId', start: 10, len: 6, type: 'int' },
  { name: 'owerId', start: 16, len: 6, type: 'int' },
  { name: 'amount', start: 22, len: 9, type: 'int' },
]

const BALANCE_SPEC = [
  { name: 'groupId', start: 0, len: 6, type: 'int' },
  { name: 'userId', start: 6, len: 6, type: 'int' },
  { name: 'totalPaid', start: 12, len: 9, type: 'int' },
  { name: 'totalOwed', start: 21, len: 9, type: 'int' },
  { name: 'netBalance', start: 30, len: 9, type: 'int' },
  { name: 'sign', start: 39, len: 1, type: 'str' },
  { name: 'asOf', start: 40, len: 14, type: 'int' },
]

module.exports = {
  USER_SPEC,
  USER_AUTH_SPEC,
  GROUP_SPEC,
  MEMBER_SPEC,
  EXPENSE_SPEC,
  SHARE_SPEC,
  BALANCE_SPEC,
}
