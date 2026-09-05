if not exists (select 1 from sys.schemas where name = N'{schema}')
    exec('create schema [{schema}]');

if not exists (select 1 from sys.tables t join sys.schemas s on t.schema_id = s.schema_id
               where s.name = N'{schema}' and t.name = N'{prefix}workflow_instances')
begin
    exec('create table [{schema}].[{prefix}workflow_instances] (
        instance_key   nvarchar(400) not null primary key,
        namespace      nvarchar(200) not null,
        workflow_name  nvarchar(200) not null,
        version        int           not null,
        instance_id    nvarchar(400) not null,
        status         tinyint       not null,
        input          varbinary(max) null,
        result         varbinary(max) null,
        error          varbinary(max) null,
        lease_owner    nvarchar(200) null,
        lease_token    uniqueidentifier null,
        lease_expires  datetimeoffset null,
        created_at     datetimeoffset not null,
        updated_at     datetimeoffset not null
    )');
    exec('create index ix_{prefix}instances_recovery on [{schema}].[{prefix}workflow_instances] (status, lease_expires)');
    exec('create index ix_{prefix}instances_query on [{schema}].[{prefix}workflow_instances] (namespace, workflow_name, version, status, created_at desc)');
end

if not exists (select 1 from sys.tables t join sys.schemas s on t.schema_id = s.schema_id
               where s.name = N'{schema}' and t.name = N'{prefix}workflow_steps')
begin
    exec('create table [{schema}].[{prefix}workflow_steps] (
        instance_key nvarchar(400) not null,
        seq          int           not null,
        name         nvarchar(200) not null,
        payload      varbinary(max) null,
        recorded_at  datetimeoffset not null,
        failed       bit not null default 0,
        primary key (instance_key, seq)
    )');
end

if not exists (select 1 from sys.tables t join sys.schemas s on t.schema_id = s.schema_id
               where s.name = N'{schema}' and t.name = N'{prefix}workflow_signals')
begin
    exec('create table [{schema}].[{prefix}workflow_signals] (
        id           bigint identity primary key,
        instance_key nvarchar(400) not null,
        name         nvarchar(200) not null,
        payload      varbinary(max) null,
        received_at  datetimeoffset not null default sysdatetimeoffset()
    )');
    exec('create index ix_{prefix}signals_lookup on [{schema}].[{prefix}workflow_signals] (instance_key, name, id)');
end
