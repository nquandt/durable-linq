create schema if not exists {schema};

create table if not exists {schema}.{prefix}workflow_instances (
    instance_key   varchar(400) primary key,
    namespace      varchar(200) not null,
    workflow_name  varchar(200) not null,
    version        int          not null,
    instance_id    varchar(400) not null,
    status         smallint     not null,        -- 0 running, 1 completed, 2 failed
    input          bytea,
    result         bytea,
    error          bytea,
    lease_owner    varchar(200),
    lease_token    uuid,
    lease_expires  timestamptz,
    created_at     timestamptz not null,
    updated_at     timestamptz not null
);
create index if not exists ix_{prefix}instances_recovery on {schema}.{prefix}workflow_instances (status, lease_expires);
create index if not exists ix_{prefix}instances_query on {schema}.{prefix}workflow_instances (namespace, workflow_name, version, status, created_at desc);

create table if not exists {schema}.{prefix}workflow_steps (
    instance_key varchar(400) not null,
    seq          int          not null,
    name         varchar(200) not null,
    payload      bytea,
    recorded_at  timestamptz not null,
    failed       boolean     not null default false,
    primary key (instance_key, seq)
);

create table if not exists {schema}.{prefix}workflow_signals (
    id           bigserial primary key,
    instance_key varchar(400) not null,
    name         varchar(200) not null,
    payload      bytea,
    received_at  timestamptz not null default now()
);
create index if not exists ix_{prefix}signals_lookup on {schema}.{prefix}workflow_signals (instance_key, name, id);
