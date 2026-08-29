# DB Normalization Summary

Goal: Reduce data redundancy and prevent data anomalies (update/insert/delete issues)
How: Separate data (into more tables)

### Terms

A CK "Candidate Key" is a key that could be the PK "Primary Key"
Every UID is effectively a CK.

A -> B is a functional dependency
A is a determinant for B (the dependent) if knowing A determines B (ZipCode -> City)

An attribute is just a col, a prime attribute is any col part of a CK.

### 0NF: Unnormalized Form (The Mess)

Repeating groups/arrays in a SINGLE cell.

| StudentID | StudentName | Courses Taken         |
| :-------- | :---------- | :-------------------- |
| 1         | Alice       | Math 101, Science 101 |

### 1NF: First Normal Form

Data must be atomic (one value per field).

Rows must be unique (via PK).

(Composite PK: StudentID + Course)

| StudentID | StudentName | Course      |
| :-------- | :---------- | :---------- |
| 1         | Alice       | Math 101    |
| 1         | Alice       | Science 101 |

The StudentName has a partial dependency on StudentID BUT NOT Course.
Since PK is ID & Course, it repeats Alice's name unnecessarily for every Course.
If Alice changes her name, you have to update all the rows that involve her courses.

### 2NF: Second Normal Form

No partial dependencies are allowed.

If a table has a composite PK (made of multiple columns)
All non-key columns must depend on EVERY col used in that key.

Students

| StudentID (PK) | StudentName |
| :------------- | :---------- |
| 1              | Alice       |

Table: Student_Courses

| StudentID (PK/FK) | Course (PK) |
| :---------------- | :---------- |
| 1                 | Math 101    |
| 1                 | Science 101 |

### 3NF: Third Normal Form

No transitive dependencies (A -> B -> C)

This means non-key cols can ONLY depend on the PK (A -> B, A -> C)

VIOLATES 3NF

| TournamentID (PK) | Year | Winner | DOB        |
| :---------------- | :--- | :----- | :--------- |
| 99                | 2024 | Alice  | 1990-05-05 |

DOB depends on the winner NOT the TournamentID

FIXES 3NF (it's also BCNF)

| TournamentID (PK) | Year | WinnerID (FK) |
| :---------------- | :--- | :------------ |
| 99                | 2024 | 1             |

| PlayerID (PK) | Name  | DateOfBirth |
| :------------ | :---- | :---------- |
| 1             | Alice | 1990-05-05  |

### BCNF: Boyce-Codd Normal Form (3NF but stricter)

Every determinant must be a CK.

VIOLATES BCNF (despite being 3NF)

A student takes a course, each course has tutors, each tutor teaches one course.

| StudentID (PK) | Course (PK) | Tutor |
| :------------- | :---------- | :---- |
| 1              | Math        | Bob   |
| 2              | Math        | Bob   |

`Tutor` determines the `Course` (since Bob only teaches Math).
`Tutor` is a determinant, but it is not a PK/CK.
If Student 1 and 2 drop Math, we lose the fact that Bob teaches Math (Deletion Anomaly).

AFTER BCNF

Fix: Split by the true determinant.

Table: Tutors

| Tutor (PK) | Course |
| :--------- | :----- |
| Bob        | Math   |

JOIN Table: Student_Tutors

| StudentID (PK) | Tutor (PK/FK) |
| :------------- | :------------ |
| 1              | Bob           |
| 2              | Bob           |

Tutor is a PK/FK because you don't want to assign the the same tutor twice.

### 3NF and BCNF difference

The only difference is you have overlapping composite CKs.

3NF allows A -> B if just one of these is met

- A is a CK
- B is part of a CK

BCNF is only met in the first condition.

An example where 3NF passes and BCNF fails

| StudentID (CK) | Course (CK) | Tutor (CK) |
| :------------- | :---------- | :--------- |
| 1              | Math        | Bob        |
| 2              | Math        | Bob        |

Each tutor teaches one course (A/Tutor -> B/Course).
Notice, Bob teaches Math is a redundancy.
If 100 students take Math, we store Bob -> Math 100x.

The PK here can be (StudentID, Course) OR (StudentID, Tutor)

> btw 2NF isn't violated because 2NF only applies to non-key cols
> Here, every col is a CK which is always a key col

3NF is fine with (StudentID, Course) even though Tutor -> Course.
BCNF is not fine with that.

BCNF is not fine if the PK is (StudentID, Tutor) either.
This is because Tutor needs to be the whole PK, not just part of the PK.

| Tutor (PK) | Course |
| :--------- | :----- |
| Bob        | Math   |

| StudentID (PK/FK) | Tutor (PK/FK) |
| :---------------- | :------------ |
| 1                 | Bob           |
| 2                 | Bob           |
