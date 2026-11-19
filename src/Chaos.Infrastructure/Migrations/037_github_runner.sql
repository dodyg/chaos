-- A null runner requires the user to choose and verify a device for an existing connection.
ALTER TABLE GitHubConnections ADD COLUMN RunnerDeviceId TEXT NULL;
ALTER TABLE GitHubConnections ADD COLUMN RunnerDeviceName TEXT NULL;
