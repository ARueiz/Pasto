\set ON_ERROR_STOP on

DO $$
DECLARE
    relation regclass;
    privilege text;
BEGIN
    IF NOT has_schema_privilege('pasto_runtime', 'public', 'USAGE')
        OR has_schema_privilege('pasto_runtime', 'public', 'CREATE')
        OR has_database_privilege('pasto_runtime', current_database(), 'CREATE, TEMP') THEN
        RAISE EXCEPTION 'Runtime schema or database privileges are incorrect';
    END IF;

    FOR relation IN
        SELECT product_table.oid::regclass FROM pg_class AS product_table
        WHERE product_table.relnamespace = 'public'::regnamespace AND product_table.relkind IN ('r', 'p')
            AND product_table.relname <> '__EFMigrationsHistory'
    LOOP
        FOREACH privilege IN ARRAY ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE'] LOOP
            IF NOT has_table_privilege('pasto_runtime', relation, privilege) THEN
                RAISE EXCEPTION 'Missing runtime % privilege on %', privilege, relation;
            END IF;
        END LOOP;
        IF has_table_privilege('pasto_runtime', relation, 'TRUNCATE') THEN
            RAISE EXCEPTION 'Runtime can truncate %', relation;
        END IF;
    END LOOP;

    relation := 'public."__EFMigrationsHistory"'::regclass;
    IF NOT has_table_privilege('pasto_runtime', relation, 'SELECT')
        OR has_table_privilege('pasto_runtime', relation, 'INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER, MAINTAIN')
        OR has_table_privilege('pasto_runtime', relation, 'SELECT WITH GRANT OPTION') THEN
        RAISE EXCEPTION 'Migration history must be read-only for the runtime role';
    END IF;

    IF EXISTS (
        SELECT 1 FROM pg_class AS relation_info
        JOIN pg_namespace AS namespace ON namespace.oid = relation_info.relnamespace
        WHERE namespace.nspname = 'public' AND relation_info.relkind IN ('r', 'p')
            AND pg_has_role('pasto_runtime', relation_info.relowner, 'MEMBER')
    ) THEN
        RAISE EXCEPTION 'Runtime role can assume ownership of product or migration tables';
    END IF;
END
$$;
