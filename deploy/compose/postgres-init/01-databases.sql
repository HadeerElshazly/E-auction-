-- The participant service keeps its own database: a bidder's national ID is
-- personal data under PDPL and must not sit in the same schema as the auction
-- catalogue that feeds public read paths.
--
-- POSTGRES_DB creates only the first one. This runs on the initial
-- initialisation of an empty data volume, which is the right moment: it must not
-- re-run over a database that already holds data.
CREATE DATABASE eauction_participant OWNER eauction;

-- The notification service keeps its own too. Its rows say which auctions a
-- bidder is registered for, when they were outbid and what they won — which is
-- the whole of what D-22 keeps off the public topics, assembled in one place.
CREATE DATABASE eauction_notifications OWNER eauction;

-- The audit service keeps its own, and for a different reason from the other
-- two: separation of duty rather than data protection. The trail has to survive
-- the compromise of the services it records, so it must not sit in a schema
-- whose credentials auction-admin or the participant service hold. Its table is
-- also append-only by trigger (see the service's InitialSchema migration), which
-- is a guarantee a shared schema's other writers would be able to lift.
CREATE DATABASE eauction_audit OWNER eauction;

-- The reporting service keeps its own read model, rebuilt from the topics. It is
-- separate for a third reason again: a report is a table scan with joins, and
-- running it against the auction service's database would let a finance query
-- compete with the approval workflow for the same locks.
CREATE DATABASE eauction_reporting OWNER eauction;
