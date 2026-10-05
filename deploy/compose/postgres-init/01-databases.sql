-- The participant service keeps its own database: a bidder's national ID is
-- personal data under PDPL and must not sit in the same schema as the auction
-- catalogue that feeds public read paths.
--
-- POSTGRES_DB creates only the first one. This runs on the initial
-- initialisation of an empty data volume, which is the right moment: it must not
-- re-run over a database that already holds data.
CREATE DATABASE eauction_participant OWNER eauction;
