# Backup Vault: schedules and recovery

Backup Vault is an evaluation-only plugin fixture. It schedules encrypted configuration archives; it does not protect media from Arrk's cleanup rules. This is not an available Arrk feature.

## Connect a destination

Open Backup Vault, choose a local directory or S3-compatible bucket and test a small upload. The process must have write access to the directory or bucket. Store bucket credentials in the plugin's credential settings. A failed connection test does not create a usable backup.

## Schedule configuration snapshots

Choose the configuration directories to include and set the schedule. A snapshot contains accounts, settings and service secrets. Encrypt archives before copying them off the host. Snapshotting running databases without a consistent database backup can produce an unusable archive. A successful job reports the archive identifier and completion time.

## Keep or expire archives

Retention removes old Backup Vault archives after a successful snapshot. It does not delete films, albums or download jobs. Mark an archive Keep to exempt it from archive expiry. Keeping an archive does not pin titles in Arrk's library. Do not reduce retention before testing recovery.

## Restore a configuration archive

Stop Arrk and the services whose databases are included before restoring. Verify the encryption password and select the intended dated archive. Restore into an empty staging directory first and check the contents. Preserve the previous configuration until login, library paths and service connections have been verified. Never restore over a live database.

## Recover an encryption password

There is no password recovery. The destination credential and archive encryption password are different secrets. Store the encryption password separately from the destination. A bucket administrator cannot decrypt archives without it. Test decryption before relying on a snapshot.

## Diagnose a failed job

Check destination permissions, free space, network access and the latest job's error. A backup job can fail even when movie downloads work. Redact credentials and archive contents before sharing logs. Retrying a failed job does not repair earlier archives automatically.
