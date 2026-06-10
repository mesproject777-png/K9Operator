using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

public static class ConvertedEndpoints
{
        public static void MapConvertedEndpoints(this WebApplication app)
    {
        app.MapGet("/", () => Results.Json(new
        {
            message = "MES API is running",
            endpoints = new[]
            {
                "/api/operator",
                "/api/workflow",
                "/api/traceability",
                "/api/labels",
                "/api/users",
                "/api/ws",
                "/api/external/sn-status"
            }
        }));

        MapOperator(app);
        MapExternal(app);
        MapWorkflow(app);
        MapTraceability(app);
        MapLabels(app);
        MapProfileUsers(app);
    }

    private static void MapProfileUsers(WebApplication app)
    {
        app.MapPut("/api/users/{id:int}", async (int id, HttpContext context) =>
        {
            var payload = await ReadJsonBodyAsync(context.Request);
            var loginId = ReadString(payload, "loginId")?.Trim();
            var password = ReadString(payload, "password");
            if (string.IsNullOrWhiteSpace(loginId) || string.IsNullOrWhiteSpace(password))
            {
                return JsonError("loginId and password are required", 400);
            }

            await using var connection = await OpenConnectionAsync();
            await EnsureWorkflowSchemaAsync(connection);
            await EnsureWorkflowStationLoginsTableAsync(connection);

            var candidates = await QueryRowsAsync(
                connection,
                """
                SELECT source_table
                FROM (
                    SELECT 'workflow_station_logins' AS source_table, l.updated_at, 0 AS source_priority
                    FROM workflow_station_logins l
                    WHERE l.id = @id
                      AND UPPER(l.station_login_id) = UPPER(@loginId)

                    UNION ALL

                    SELECT 'item_routing_steps' AS source_table, r.updated_at, 1 AS source_priority
                    FROM item_routing_steps r
                    WHERE r.id = @id
                      AND UPPER(r.station_login_id) = UPPER(@loginId)

                    UNION ALL

                    SELECT 'workflow_routing_steps' AS source_table, r.updated_at, 2 AS source_priority
                    FROM workflow_routing_steps r
                    WHERE r.id = @id
                      AND UPPER(r.station_login_id) = UPPER(@loginId)
                ) station
                ORDER BY station.source_priority ASC, station.updated_at DESC
                LIMIT 1
                """,
                ("id", id),
                ("loginId", loginId));

            if (candidates.Count == 0)
            {
                return JsonError("Operator profile not found", 404);
            }

            var sourceTable = candidates[0]["source_table"]?.ToString();
            var affected = sourceTable switch
            {
                "workflow_station_logins" => await ExecuteAsync(
                    connection,
                    """
                    UPDATE workflow_station_logins
                    SET station_login_password = @password,
                        updated_at = NOW()
                    WHERE id = @id
                      AND UPPER(station_login_id) = UPPER(@loginId)
                    """,
                    ("password", password),
                    ("id", id),
                    ("loginId", loginId)),
                "item_routing_steps" => await ExecuteAsync(
                    connection,
                    """
                    UPDATE item_routing_steps
                    SET station_login_password = @password,
                        updated_at = NOW()
                    WHERE id = @id
                      AND UPPER(station_login_id) = UPPER(@loginId)
                    """,
                    ("password", password),
                    ("id", id),
                    ("loginId", loginId)),
                "workflow_routing_steps" => await ExecuteAsync(
                    connection,
                    """
                    UPDATE workflow_routing_steps
                    SET station_login_password = @password,
                        updated_at = NOW()
                    WHERE id = @id
                      AND UPPER(station_login_id) = UPPER(@loginId)
                    """,
                    ("password", password),
                    ("id", id),
                    ("loginId", loginId)),
                _ => 0
            };

            return affected == 0
                ? JsonError("Operator profile not found", 404)
                : Results.Json(new { message = "Password updated successfully" });
        });
    }

    private static void MapLabels(WebApplication app)
    {
        app.MapGet("/api/labels", async () =>
        {
            await using var connection = await OpenConnectionAsync();
            var rows = await QueryRowsAsync(
                connection,
                """
                SELECT
                  id,
                  label_code,
                  COALESCE(label_description, '') AS label_description,
                  COALESCE(status, 'Active') AS status
                FROM label_masters
                ORDER BY label_code ASC, id ASC
                """);

            return Results.Json(new { data = rows });
        });

        app.MapGet("/api/labels/{id:int}", async (int id) =>
        {
            await using var connection = await OpenConnectionAsync();
            var rows = await QueryRowsAsync(
                connection,
                """
                SELECT
                  lm.id,
                  lm.label_code,
                  COALESCE(lm.label_description, '') AS label_description,
                  COALESCE(lm.status, 'Active') AS status,
                  latest.id AS prn_template_id,
                  latest.prn_file_name,
                  latest.prn_content
                FROM label_masters lm
                LEFT JOIN LATERAL (
                  SELECT id, prn_file_name, prn_content
                  FROM label_prn_templates
                  WHERE label_master_id = lm.id
                    AND COALESCE(NULLIF(TRIM(prn_content), ''), '') <> ''
                  ORDER BY version DESC, id DESC
                  LIMIT 1
                ) latest ON TRUE
                WHERE lm.id = @id
                LIMIT 1
                """,
                ("id", id));

            if (rows.Count == 0)
            {
                return JsonError("Label not found", 404);
            }

            var row = rows[0];
            var prnContent = Convert.ToString(row["prn_content"]) ?? string.Empty;
            var result = new Dictionary<string, object?>
            {
                ["id"] = row["id"],
                ["label_code"] = row["label_code"],
                ["label_description"] = row["label_description"],
                ["status"] = row["status"],
                ["prn_content"] = prnContent,
                ["prn_template"] = row["prn_template_id"] is null
                    ? null
                    : new Dictionary<string, object?>
                    {
                        ["id"] = row["prn_template_id"],
                        ["label_master_id"] = row["id"],
                        ["prn_file_name"] = row["prn_file_name"],
                        ["prn_content"] = prnContent
                    }
            };

            return Results.Json(result);
        });
    }

            private static void MapOperator(WebApplication app)
    {
        app.MapPost("/api/operator/fail", async (HttpContext context) =>
        {
            var payload = await ReadJsonBodyAsync(context.Request);
            var query = ReadString(payload, "query")?.Trim();
            var loginId = ReadString(payload, "loginId")?.Trim();
            var debugRemark = ReadString(payload, "remark")?.Trim();
            var requestedWorkflowPartId = ReadInt(payload, "workflowPartId");
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(loginId))
            {
                return JsonMessage("Serial number and login ID are required", 400);
            }

            await using var connection = await OpenConnectionAsync();
            await EnsureWorkflowSchemaAsync(connection);
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                var operatorStation = await GetOperatorStationByLoginAsync(connection, loginId, requestedWorkflowPartId);
                if (operatorStation is null)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("Station login session is no longer assigned. Please login again", 401);
                }

                var serial = await GetWorkflowSerialByQueryAsync(connection, query);
                if (serial is null)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("SN/RSN not found", 404);
                }

                var workflowPartId = Convert.ToInt32(serial["workflow_part_id"]);
                if (Convert.ToInt32(operatorStation["workflow_part_id"]) != workflowPartId)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("This station login is not assigned to this serial number part number", 409);
                }

                var routeRows = await GetWorkflowRouteRowsForPartAsync(connection, workflowPartId);
                var selected = routeRows.FirstOrDefault(step =>
                    string.Equals(step["station_code"]?.ToString(), operatorStation["station_code"]?.ToString(), StringComparison.OrdinalIgnoreCase));
                if (selected is null)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("Logged-in station is not in this serial route", 409);
                }

                var currentOrder = ResolveCurrentOrder(serial, routeRows);
                var selectedOrder = Convert.ToInt32(selected["station_order"]);
                var serialStatus = serial["serial_status"]?.ToString();
                if (selectedOrder < currentOrder || (selectedOrder == currentOrder && string.Equals(serialStatus, "Completed", StringComparison.OrdinalIgnoreCase)))
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("Station is already passed", 409);
                }

                var pendingRepairStep = FindPendingRepairStep(serial, routeRows, selected);
                if (pendingRepairStep is not null)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage($"Repair station \"{GetStationDisplayName(pendingRepairStep)}\" is not passed. Please pass repair station before continuing.", 409);
                }

                var blockingStep = FindBlockingRequiredStep(routeRows, currentOrder, selectedOrder);
                if (blockingStep is not null)
                {
                    var stationName = GetStationDisplayName(blockingStep);
                    await transaction.RollbackAsync();
                    return JsonMessage($"Previous station \"{stationName}\" is failed. Please pass that station before continuing.", 409);
                }

                var serialNumber = serial["sn"]?.ToString() ?? query;
                var stationNameForMessage = GetStationDisplayName(selected);
                var existingFailureCount = await GetContinuousWorkflowFailureCountAsync(connection, serial["id"]!, selected["station_code"]?.ToString());
                if (existingFailureCount >= 3)
                {
                    var existingRepairConfig = await GetWorkflowRepairStationConfigAsync(connection, workflowPartId, selected["station_code"]?.ToString());
                    var existingRepairStep = ResolveRepairRouteStep(routeRows, existingRepairConfig?["repair_station_name"]?.ToString());
                    if (existingRepairConfig is not null &&
                        existingRepairConfig["is_repair_station_enabled"] is bool existingRepairEnabled && existingRepairEnabled &&
                        existingRepairStep is not null)
                    {
                        await transaction.RollbackAsync();
                        return JsonMessage($"{serialNumber} went to repair station {GetStationDisplayName(existingRepairStep)}.", 409);
                    }

                    await transaction.RollbackAsync();
                    return JsonMessage($"{serialNumber} is already failed three times in station {stationNameForMessage}.", 409);
                }

                var failureCount = existingFailureCount + 1;
                var repairConfig = await GetWorkflowRepairStationConfigAsync(connection, workflowPartId, selected["station_code"]?.ToString());
                var repairStep = failureCount >= 3
                    ? ResolveRepairRouteStep(routeRows, repairConfig?["repair_station_name"]?.ToString())
                    : null;
                var shouldMoveToRepair = failureCount >= 3 && repairConfig is not null &&
                    repairConfig["is_repair_station_enabled"] is bool enabled && enabled &&
                    repairStep is not null;
                var nextStatus = shouldMoveToRepair ? "Repair" : "Failed";
                var afterStationCode = shouldMoveToRepair ? repairStep!["station_code"] : selected["station_code"];
                var afterStationOrder = shouldMoveToRepair ? repairStep!["station_order"] : selected["station_order"];
                var remark = shouldMoveToRepair
                    ? $"{serialNumber} is failed three times, so it went to repair station {GetStationDisplayName(repairStep!)}."
                    : $"SN is failed for {ToOrdinal(failureCount)} time in station {stationNameForMessage}.";

                await ExecuteAsync(
                    connection,
                    """
                    UPDATE workflow_serial_numbers
                    SET status = @status,
                        condition = 'NG',
                        current_station_code = @stationCode,
                        current_station_order = @stationOrder,
                        last_moved_at = NOW(),
                        updated_at = NOW()
                    WHERE id = @id
                    """,
                    ("status", nextStatus),
                    ("stationCode", afterStationCode),
                    ("stationOrder", afterStationOrder),
                    ("id", serial["id"]));

                await InsertWorkflowStationLogAsync(
                    connection,
                    serial,
                    selected,
                    "FAIL",
                    remark,
                    loginId,
                    serial["current_station_code"],
                    serial["current_station_order"],
                    afterStationCode,
                    afterStationOrder,
                    debugRemark);

                await transaction.CommitAsync();
                return Results.Json(new
                {
                    message = shouldMoveToRepair
                        ? remark
                        : $"{serialNumber} ({serialNumber}) is failed in station name {stationNameForMessage}.",
                    fail_count = failureCount,
                    status = nextStatus,
                    repair_station = shouldMoveToRepair ? GetStationDisplayName(repairStep!) : null
                });
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });

        app.MapPost("/api/operator/login", async (HttpContext context) =>
        {
            var payload = await ReadJsonBodyAsync(context.Request);
            var loginId = ReadString(payload, "loginId")?.Trim();
            var password = ReadString(payload, "password");
            if (string.IsNullOrWhiteSpace(loginId) || string.IsNullOrWhiteSpace(password))
            {
                return JsonError("loginId and password are required", 400);
            }

            await using var connection = await OpenConnectionAsync();
            await EnsureWorkflowSchemaAsync(connection);
            await EnsureWorkflowStationLoginsTableAsync(connection);
            var rows = await QueryRowsAsync(
                connection,
                """
                SELECT
                  station.id,
                  station.login_id,
                  station.station_login_password,
                  station.station_code,
                  station.station_name,
                  station.workflow_part_id,
                  station.box_qty,
                  station.pn,
                  station.workflow_work_order_id,
                  station.wo
                FROM (
                    SELECT
                      l.id,
                      l.station_login_id AS login_id,
                      l.station_login_password,
                      r.station_code,
                      r.station_name,
                      r.workflow_part_id,
                      p.box_qty,
                      p.pn,
                      COALESCE(w.id, latest_w.id) AS workflow_work_order_id,
                      COALESCE(w.wo, latest_w.wo) AS wo,
                      COALESCE(w.updated_at, latest_w.updated_at) AS workflow_work_order_updated_at,
                      l.updated_at,
                      0 AS source_priority
                    FROM workflow_station_logins l
                    JOIN workflow_routing_steps r ON r.id = l.workflow_routing_step_id
                    JOIN workflow_part_numbers p ON p.id = r.workflow_part_id
                    LEFT JOIN workflow_work_orders w ON w.id = l.workflow_work_order_id
                    LEFT JOIN LATERAL (
                        SELECT ww.id, ww.wo, ww.updated_at
                        FROM workflow_work_orders ww
                        WHERE ww.workflow_part_id = p.id
                        ORDER BY ww.updated_at DESC NULLS LAST, ww.id DESC
                        LIMIT 1
                    ) latest_w ON TRUE
                    WHERE UPPER(l.station_login_id) = UPPER(@loginId)
                      AND l.station_login_password = @password

                    UNION ALL

                    SELECT
                      r.id,
                      r.station_login_id AS login_id,
                      r.station_login_password,
                      r.station_code,
                      r.station_name,
                      p.id AS workflow_part_id,
                      p.box_qty,
                      i.pn,
                      w.id AS workflow_work_order_id,
                      w.wo,
                      w.updated_at AS workflow_work_order_updated_at,
                      r.updated_at,
                      1 AS source_priority
                    FROM item_routing_steps r
                    JOIN items i ON i.id = r.item_id
                    LEFT JOIN workflow_part_numbers p ON UPPER(p.pn) = UPPER(i.pn)
                    LEFT JOIN LATERAL (
                        SELECT ww.id, ww.wo, ww.updated_at
                        FROM workflow_work_orders ww
                        WHERE ww.workflow_part_id = p.id
                        ORDER BY ww.updated_at DESC NULLS LAST, ww.id DESC
                        LIMIT 1
                    ) w ON TRUE
                    WHERE UPPER(r.station_login_id) = UPPER(@loginId)
                      AND r.station_login_password = @password

                    UNION ALL

                    SELECT
                      r.id,
                      r.station_login_id AS login_id,
                      r.station_login_password,
                      r.station_code,
                      r.station_name,
                      r.workflow_part_id,
                      p.box_qty,
                      p.pn,
                      w.id AS workflow_work_order_id,
                      w.wo,
                      w.updated_at AS workflow_work_order_updated_at,
                      r.updated_at,
                      2 AS source_priority
                    FROM workflow_routing_steps r
                    JOIN workflow_part_numbers p ON p.id = r.workflow_part_id
                    LEFT JOIN workflow_work_orders w ON w.workflow_part_id = p.id
                    WHERE UPPER(r.station_login_id) = UPPER(@loginId)
                      AND r.station_login_password = @password
                ) station
                ORDER BY station.source_priority ASC,
                         station.workflow_work_order_updated_at DESC NULLS LAST,
                         station.updated_at DESC,
                         station.id DESC
                LIMIT 1
                """,
                ("loginId", loginId),
                ("password", password));

            if (rows.Count == 0)
            {
                return JsonError("Invalid station login ID or password", 401);
            }

            var station = rows[0];
            if (!string.Equals(station["station_login_password"]?.ToString(), password, StringComparison.Ordinal))
            {
                return JsonError("Invalid station login ID or password", 401);
            }

            return Results.Json(new
            {
                id = station["id"],
                login_id = station["login_id"],
                user_name = station["station_name"],
                is_active = true,
                created_at = DateTime.UtcNow,
                role_id = 0,
                role_name = "Operator",
                page_access = new[] { "dashboard/operator" },
                station_code = station["station_code"],
                station_name = station["station_name"],
                workflow_part_id = station["workflow_part_id"],
                workflow_work_order_id = station["workflow_work_order_id"],
                pn = station["pn"],
                wo = station["wo"],
                box_qty = station["box_qty"],
                is_pack_station = IsPackStation(station["station_code"]?.ToString(), station["station_name"]?.ToString())
            });
        });

        app.MapGet("/api/operator/label-printing-config", async (HttpRequest request) =>
        {
            var loginId = request.Query["loginId"].ToString().Trim();
            var stationCodeFilter = request.Query["stationCode"].ToString().Trim();
            var workflowPartIdFilter = int.TryParse(request.Query["workflowPartId"].ToString(), out var parsedWorkflowPartId)
                ? parsedWorkflowPartId
                : (int?)null;
            if (string.IsNullOrWhiteSpace(loginId))
            {
                return JsonError("loginId is required", 400);
            }

            await using var connection = await OpenConnectionAsync();
            await EnsureWorkflowSchemaAsync(connection);
            var station = await GetOperatorStationByLoginAsync(connection, loginId, workflowPartIdFilter, stationCodeFilter);
            if (station is null)
            {
                return JsonError("Invalid station login ID", 404);
            }

            var config = await GetWorkflowStationLabelPrintingConfigAsync(
                connection,
                Convert.ToInt32(station["workflow_part_id"]),
                station["station_code"]?.ToString());

            if (config is null || config["isLabelPrintingEnabled"] is not bool enabled || !enabled)
            {
                return Results.Json(new { isLabelPrintingEnabled = false });
            }

            return Results.Json(BuildOperatorLabelPrintingResponse(config, station));
        });

        app.MapGet("/api/operator/weighing-config", async (HttpRequest request) =>
        {
            var loginId = request.Query["loginId"].ToString().Trim();
            var stationCodeFilter = request.Query["stationCode"].ToString().Trim();
            var workflowPartIdFilter = int.TryParse(request.Query["workflowPartId"].ToString(), out var parsedWorkflowPartId)
                ? parsedWorkflowPartId
                : (int?)null;
            if (string.IsNullOrWhiteSpace(loginId))
            {
                return JsonError("loginId is required", 400);
            }

            await using var connection = await OpenConnectionAsync();
            await EnsureWorkflowSchemaAsync(connection);
            var station = await GetOperatorStationByLoginAsync(connection, loginId, workflowPartIdFilter, stationCodeFilter);
            if (station is null)
            {
                return JsonError("Invalid station login ID", 404);
            }

            var config = await GetWorkflowStationWeighingConfigAsync(
                connection,
                Convert.ToInt32(station["workflow_part_id"]),
                station["station_code"]?.ToString());

            if (config is null || config["isWeighingEnabled"] is not bool enabled || !enabled)
            {
                return Results.Json(new { isWeighingEnabled = false });
            }

            return Results.Json(new
            {
                isWeighingEnabled = true,
                stationCode = station["station_code"],
                stationName = station["station_name"],
                workflowPartId = station["workflow_part_id"],
                minimumWeight = config["minimumWeight"],
                maximumWeight = config["maximumWeight"],
                tolerance = config["tolerance"]
            });
        });

        app.MapGet("/api/operator/sampling/status", async (HttpRequest request) =>
        {
            var query = request.Query["query"].ToString().Trim();
            var loginId = request.Query["loginId"].ToString().Trim();
            var workflowPartIdFilter = int.TryParse(request.Query["workflowPartId"].ToString(), out var parsedWorkflowPartId)
                ? parsedWorkflowPartId
                : (int?)null;
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(loginId))
            {
                return JsonMessage("Serial number and login ID are required", 400);
            }

            await using var connection = await OpenConnectionAsync();
            await EnsureWorkflowSchemaAsync(connection);
            var contextResult = await ResolveOperatorWorkflowContextAsync(connection, query, loginId, workflowPartIdFilter);
            if (contextResult.Error is not null)
            {
                return contextResult.Error;
            }

            var operatorContext = contextResult.Context!;
            var station = operatorContext.Selected;
            var serial = operatorContext.Serial;
            var samplingDecision = await ResolveSamplingDecisionAsync(
                connection,
                Convert.ToInt32(serial["workflow_part_id"]),
                station,
                serial);
            var isSamplingStation = string.Equals(station["sample_mode"]?.ToString(), "Sample", StringComparison.OrdinalIgnoreCase);
            var groupStart = samplingDecision.IntervalQty > 0
                ? ((samplingDecision.GeneratedIndex - 1) / samplingDecision.IntervalQty) * samplingDecision.IntervalQty + 1
                : samplingDecision.GeneratedIndex;
            var groupEnd = samplingDecision.IntervalQty > 0
                ? groupStart + samplingDecision.IntervalQty - 1
                : samplingDecision.GeneratedIndex;

            return Results.Json(new
            {
                stationCode = station["station_code"],
                stationName = station["station_name"],
                sampleMode = station["sample_mode"],
                isSamplingStation,
                isEnabled = samplingDecision.IsEnabled,
                isRequired = samplingDecision.IsRequired,
                samplingType = samplingDecision.SamplingType,
                reason = samplingDecision.Reason,
                generatedIndex = samplingDecision.GeneratedIndex,
                intervalQty = samplingDecision.IntervalQty,
                sampleQty = samplingDecision.SampleQty,
                lotSize = samplingDecision.LotSize,
                groupStart,
                groupEnd,
                serial = new
                {
                    id = serial["id"],
                    sn = serial["sn"],
                    rsn = serial["rsn"],
                    pn = serial["pn"],
                    wo = serial["wo"]
                }
            });
        });

        app.MapPut("/api/operator/label-printing-config", async (HttpContext context) =>
        {
            var payload = await ReadJsonBodyAsync(context.Request);
            var loginId = ReadString(payload, "loginId")?.Trim();
            var workflowPartIdFilter = ReadInt(payload, "workflowPartId");
            var stationCodeFilter = ReadString(payload, "stationCode")?.Trim();
            var printerIp = FirstNonEmpty(
                ReadString(payload, "ipAddress")?.Trim() ?? string.Empty,
                ReadString(payload, "printerIp")?.Trim() ?? string.Empty);
            var port = ReadString(payload, "port")?.Trim();

            if (string.IsNullOrWhiteSpace(loginId))
            {
                return JsonError("loginId is required", 400);
            }

            if (string.IsNullOrWhiteSpace(printerIp))
            {
                return JsonError("Printer IP is required", 400);
            }

            await using var connection = await OpenConnectionAsync();
            await EnsureWorkflowSchemaAsync(connection);
            var station = await GetOperatorStationByLoginAsync(connection, loginId, workflowPartIdFilter, stationCodeFilter);
            if (station is null)
            {
                return JsonError("Invalid station login ID", 404);
            }

            var workflowPartId = Convert.ToInt32(station["workflow_part_id"]);
            var stationCode = station["station_code"]?.ToString();
            var existingConfig = await GetWorkflowStationLabelPrintingConfigAsync(connection, workflowPartId, stationCode);
            if (existingConfig is null || existingConfig["isLabelPrintingEnabled"] is not bool enabled || !enabled)
            {
                return JsonError("Label printing is not enabled for this station", 404);
            }

            var printerPort = ParsePositiveInt(port, ParsePositiveInt(existingConfig["port"], 9100)).ToString(CultureInfo.InvariantCulture);
            await UpdateWorkflowStationPrinterAsync(connection, workflowPartId, stationCode, printerIp, printerPort);

            var config = await GetWorkflowStationLabelPrintingConfigAsync(connection, workflowPartId, stationCode);
            return Results.Json(BuildOperatorLabelPrintingResponse(config!, station, "Printer saved"));
        });

        app.MapPost("/api/operator/label-printing-config/test-connection", async (HttpContext context) =>
        {
            var payload = await ReadJsonBodyAsync(context.Request);
            var loginId = ReadString(payload, "loginId")?.Trim();
            var workflowPartIdFilter = ReadInt(payload, "workflowPartId");
            var stationCodeFilter = ReadString(payload, "stationCode")?.Trim();
            var printerIp = FirstNonEmpty(
                ReadString(payload, "ipAddress")?.Trim() ?? string.Empty,
                ReadString(payload, "printerIp")?.Trim() ?? string.Empty);
            var port = ReadString(payload, "port")?.Trim();

            if (string.IsNullOrWhiteSpace(loginId))
            {
                return JsonError("loginId is required", 400);
            }

            if (string.IsNullOrWhiteSpace(printerIp))
            {
                return JsonError("Printer IP is required", 400);
            }

            await using var connection = await OpenConnectionAsync();
            await EnsureWorkflowSchemaAsync(connection);
            var station = await GetOperatorStationByLoginAsync(connection, loginId, workflowPartIdFilter, stationCodeFilter);
            if (station is null)
            {
                return JsonError("Invalid station login ID", 404);
            }

            var workflowPartId = Convert.ToInt32(station["workflow_part_id"]);
            var stationCode = station["station_code"]?.ToString();
            var existingConfig = await GetWorkflowStationLabelPrintingConfigAsync(connection, workflowPartId, stationCode);
            if (existingConfig is null || existingConfig["isLabelPrintingEnabled"] is not bool enabled || !enabled)
            {
                return JsonError("Label printing is not enabled for this station", 404);
            }

            var printerPort = ParsePositiveInt(port, ParsePositiveInt(existingConfig["port"], 9100));
            try
            {
                await TestPrinterConnectionAsync(printerIp, printerPort);
                await UpdateWorkflowStationPrinterAsync(connection, workflowPartId, stationCode, printerIp, printerPort.ToString(CultureInfo.InvariantCulture), "Online");
                var config = await GetWorkflowStationLabelPrintingConfigAsync(connection, workflowPartId, stationCode);
                return Results.Json(BuildOperatorLabelPrintingResponse(config!, station, "Connected", true));
            }
            catch
            {
                await UpdateWorkflowStationPrinterAsync(connection, workflowPartId, stationCode, printerIp, printerPort.ToString(CultureInfo.InvariantCulture), "Offline");
                var config = await GetWorkflowStationLabelPrintingConfigAsync(connection, workflowPartId, stationCode);
                return Results.Json(BuildOperatorLabelPrintingResponse(config!, station, "Connection failed", false));
            }
        });

        app.MapPost("/api/operator/label-printing-config/test-print", async (HttpContext context) =>
        {
            var payload = await ReadJsonBodyAsync(context.Request);
            var loginId = ReadString(payload, "loginId")?.Trim();
            var workflowPartIdFilter = ReadInt(payload, "workflowPartId");
            var stationCodeFilter = ReadString(payload, "stationCode")?.Trim();
            var printerIp = FirstNonEmpty(
                ReadString(payload, "ipAddress")?.Trim() ?? string.Empty,
                ReadString(payload, "printerIp")?.Trim() ?? string.Empty);
            var port = ReadString(payload, "port")?.Trim();

            if (string.IsNullOrWhiteSpace(loginId))
            {
                return JsonError("loginId is required", 400);
            }

            if (string.IsNullOrWhiteSpace(printerIp))
            {
                return JsonError("Printer IP is required", 400);
            }

            await using var connection = await OpenConnectionAsync();
            await EnsureWorkflowSchemaAsync(connection);
            var station = await GetOperatorStationByLoginAsync(connection, loginId, workflowPartIdFilter, stationCodeFilter);
            if (station is null)
            {
                return JsonError("Invalid station login ID", 404);
            }

            var workflowPartId = Convert.ToInt32(station["workflow_part_id"]);
            var stationCode = station["station_code"]?.ToString();
            var existingConfig = await GetWorkflowStationLabelPrintingConfigAsync(connection, workflowPartId, stationCode);
            if (existingConfig is null || existingConfig["isLabelPrintingEnabled"] is not bool enabled || !enabled)
            {
                return JsonError("Label printing is not enabled for this station", 404);
            }

            var labelCode = ReadDictionaryText(existingConfig, "labelCode");
            if (string.IsNullOrWhiteSpace(labelCode))
            {
                return JsonError("Label Code is missing for this station", 400);
            }

            var prnContent = await GetLatestLabelPrnTemplateByCodeAsync(connection, labelCode);
            if (string.IsNullOrWhiteSpace(prnContent))
            {
                return JsonError("PRN template is missing for this Label Code", 404);
            }

            var printerPort = ParsePositiveInt(port, ParsePositiveInt(existingConfig["port"], 9100));
            await UpdateWorkflowStationPrinterAsync(connection, workflowPartId, stationCode, printerIp, printerPort.ToString(CultureInfo.InvariantCulture));

            try
            {
                var renderedPrn = ApplyWorkflowLabelPlaceholders(prnContent, BuildOperatorTestLabelSerial(station), station);
                await SendRawPrinterDataAsync(printerIp, printerPort, renderedPrn);
                await UpdateWorkflowStationPrinterAsync(connection, workflowPartId, stationCode, printerIp, printerPort.ToString(CultureInfo.InvariantCulture), "Online");
                var config = await GetWorkflowStationLabelPrintingConfigAsync(connection, workflowPartId, stationCode);
                return Results.Json(BuildOperatorLabelPrintingResponse(config!, station, "Test print sent", true));
            }
            catch
            {
                await UpdateWorkflowStationPrinterAsync(connection, workflowPartId, stationCode, printerIp, printerPort.ToString(CultureInfo.InvariantCulture), "Offline");
                var config = await GetWorkflowStationLabelPrintingConfigAsync(connection, workflowPartId, stationCode);
                return Results.Json(BuildOperatorLabelPrintingResponse(config!, station, "Test print failed", false));
            }
        });

        app.MapGet("/api/operator/assembly/status", async (HttpRequest request) =>
        {
            var parentQuery = request.Query["parent_query"].ToString().Trim();
            var loginId = request.Query["loginId"].ToString().Trim();
            var workflowPartId = int.TryParse(request.Query["workflowPartId"].ToString(), out var parsedWorkflowPartId)
                ? parsedWorkflowPartId
                : (int?)null;
            if (string.IsNullOrWhiteSpace(parentQuery) || string.IsNullOrWhiteSpace(loginId))
            {
                return JsonMessage("Parent serial number and login ID are required", 400);
            }

            await using var connection = await OpenConnectionAsync();
            await EnsureWorkflowSchemaAsync(connection);
            var contextResult = await ResolveOperatorWorkflowContextAsync(connection, parentQuery, loginId, workflowPartId);
            if (contextResult.Error is not null)
            {
                return contextResult.Error;
            }

            var status = await BuildWorkflowBomBindingStatusAsync(connection, contextResult.Context!.Serial, contextResult.Context.Selected);
            return Results.Json(status.Payload);
        });

        app.MapPost("/api/operator/assembly/bind", async (HttpContext context) =>
        {
            var payload = await ReadJsonBodyAsync(context.Request);
            var parentQuery = ReadString(payload, "parent_query")?.Trim() ?? ReadString(payload, "parent_sn")?.Trim();
            var childQuery = ReadString(payload, "child_query")?.Trim() ?? ReadString(payload, "child_sn")?.Trim();
            var loginId = ReadString(payload, "loginId")?.Trim();
            var workflowPartId = ReadInt(payload, "workflowPartId");
            if (string.IsNullOrWhiteSpace(parentQuery) || string.IsNullOrWhiteSpace(childQuery) || string.IsNullOrWhiteSpace(loginId))
            {
                return JsonMessage("Parent serial number, child serial number, and login ID are required", 400);
            }

            await using var connection = await OpenConnectionAsync();
            await EnsureWorkflowSchemaAsync(connection);
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                var contextResult = await ResolveOperatorWorkflowContextAsync(connection, parentQuery, loginId, workflowPartId);
                if (contextResult.Error is not null)
                {
                    await transaction.RollbackAsync();
                    return contextResult.Error;
                }

                var operatorContext = contextResult.Context!;
                var parentSerial = operatorContext.Serial;
                var selectedStation = operatorContext.Selected;
                var childSerial = await GetWorkflowSerialByQueryAsync(connection, childQuery);
                if (childSerial is null)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("Child SN/RSN not found", 404);
                }

                if (Convert.ToInt64(parentSerial["id"]) == Convert.ToInt64(childSerial["id"]))
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("Parent and child serial cannot be same", 400);
                }

                if (!string.Equals(childSerial["serial_status"]?.ToString(), "Completed", StringComparison.OrdinalIgnoreCase))
                {
                    await transaction.RollbackAsync();
                    return JsonMessage($"Child \"{childSerial["sn"]}\" not passed all stations", 409);
                }

                var requiredRows = await GetWorkflowBomLinesForStationAsync(
                    connection,
                    Convert.ToInt32(parentSerial["workflow_part_id"]),
                    selectedStation["station_code"]?.ToString() ?? string.Empty);
                if (requiredRows.Count == 0)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("No BOM child serial is required at this station", 409);
                }

                var existingChildBinding = await ScalarAsync<long?>(
                    connection,
                    "SELECT id FROM workflow_serial_bom_bindings WHERE child_workflow_serial_id = @childId LIMIT 1",
                    ("childId", childSerial["id"]));
                if (existingChildBinding is not null)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("Child serial is already bound", 409);
                }

                var bindings = await GetWorkflowBomBindingsForParentStationAsync(
                    connection,
                    parentSerial["id"]!,
                    selectedStation["station_code"]?.ToString() ?? string.Empty);
                var boundByLine = bindings
                    .GroupBy(row => Convert.ToInt32(row["workflow_bom_child_id"]))
                    .ToDictionary(group => group.Key, group => group.Count());
                var childPn = childSerial["pn"]?.ToString() ?? string.Empty;
                var matchingLine = requiredRows.FirstOrDefault(row =>
                {
                    var requiredPn = row["son_pn"]?.ToString() ?? string.Empty;
                    var lineId = Convert.ToInt32(row["id"]);
                    var qty = Convert.ToInt32(row["qty"]);
                    var boundQty = boundByLine.TryGetValue(lineId, out var count) ? count : 0;
                    return boundQty < qty && string.Equals(requiredPn, childPn, StringComparison.OrdinalIgnoreCase);
                });

                if (matchingLine is null)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage($"Scanned child PN {childPn} is not pending for this station BOM", 409);
                }

                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO workflow_serial_bom_bindings
                      (parent_workflow_serial_id, child_workflow_serial_id, workflow_bom_child_id,
                       station_code, station_name, created_by)
                    VALUES
                      (@parentId, @childId, @bomChildId, @stationCode, @stationName, @createdBy)
                    """,
                    ("parentId", parentSerial["id"]),
                    ("childId", childSerial["id"]),
                    ("bomChildId", matchingLine["id"]),
                    ("stationCode", selectedStation["station_code"]),
                    ("stationName", selectedStation["station_name"]),
                    ("createdBy", loginId));

                var status = await BuildWorkflowBomBindingStatusAsync(connection, parentSerial, selectedStation);
                await transaction.CommitAsync();
                return Results.Json(status.Payload, statusCode: 201);
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                await transaction.RollbackAsync();
                return JsonMessage("Child serial is already bound", 409);
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });

        app.MapGet("/api/operator/multibox/status", async (HttpRequest request) =>
        {
            var loginId = request.Query["loginId"].ToString().Trim();
            var workflowPartIdFilter = int.TryParse(request.Query["workflowPartId"].ToString(), out var parsedWorkflowPartId)
                ? parsedWorkflowPartId
                : (int?)null;
            if (string.IsNullOrWhiteSpace(loginId))
            {
                return JsonMessage("Login ID is required", 400);
            }

            await using var connection = await OpenConnectionAsync();
            await EnsureWorkflowSchemaAsync(connection);
            var station = await GetOperatorStationByLoginAsync(connection, loginId, workflowPartIdFilter);
            if (station is null)
            {
                return JsonMessage("Invalid station login ID", 401);
            }

            if (!IsPackStation(station["station_code"]?.ToString(), station["station_name"]?.ToString()))
            {
                return Results.Json(new { enabled = false });
            }

            var boxQty = station["box_qty"] is null ? 0 : Convert.ToInt32(station["box_qty"]);
            if (boxQty <= 0)
            {
                return Results.Json(new { enabled = false });
            }

            var box = await GetOrCreateOpenWorkflowBoxAsync(connection, Convert.ToInt32(station["workflow_part_id"]), station["workflow_work_order_id"], loginId);
            var scannedQty = await ScalarAsync<int>(connection, "SELECT COUNT(*)::int FROM workflow_multibox_items WHERE box_id = @boxId", ("boxId", box["id"]));
            var items = await GetWorkflowBoxItemsAsync(connection, box["id"]);
            return Results.Json(new
            {
                enabled = true,
                box_qty = boxQty,
                scanned_qty = scannedQty,
                remaining_qty = Math.Max(boxQty - scannedQty, 0),
                box_no = box["box_no"],
                is_closed = string.Equals(box["status"]?.ToString(), "CLOSED", StringComparison.OrdinalIgnoreCase),
                items
            });
        });

        app.MapPost("/api/operator/multibox/scan", async (HttpContext context) =>
        {
            var payload = await ReadJsonBodyAsync(context.Request);
            var loginId = ReadString(payload, "loginId")?.Trim();
            var query = ReadString(payload, "query")?.Trim();
            var workflowPartIdFilter = ReadInt(payload, "workflowPartId");
            if (string.IsNullOrWhiteSpace(loginId) || string.IsNullOrWhiteSpace(query))
            {
                return JsonMessage("Login ID and serial number are required", 400);
            }

            await using var connection = await OpenConnectionAsync();
            await EnsureWorkflowSchemaAsync(connection);
            await EnsureSerialTrackingSchemaAsync(connection);
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                var station = await GetOperatorStationByLoginAsync(connection, loginId, workflowPartIdFilter);
                if (station is null)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("Invalid station login ID", 401);
                }

                if (!IsPackStation(station["station_code"]?.ToString(), station["station_name"]?.ToString()))
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("Multibox is available only for Pack station", 403);
                }

                var boxQty = station["box_qty"] is null ? 0 : Convert.ToInt32(station["box_qty"]);
                if (boxQty <= 0)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("Box Qty is not configured", 400);
                }

                var serial = await GetWorkflowSerialByQueryAsync(connection, query);
                if (serial is null)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("SN/RSN not found", 404);
                }

                if (Convert.ToInt32(serial["workflow_part_id"]) != Convert.ToInt32(station["workflow_part_id"]))
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("This serial number is not assigned to this pack station part number", 409);
                }

                var alreadyPackedInPackage = await ScalarAsync<int>(
                    connection,
                    """
                    SELECT COUNT(*)::int
                    FROM packing_package_items ppi
                    JOIN serial_numbers sn ON sn.id = ppi.serial_id
                    WHERE UPPER(sn.sn) = UPPER(@query)
                       OR UPPER(sn.rsn) = UPPER(@query)
                    """,
                    ("query", query));
                if (alreadyPackedInPackage > 0)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("This serial number is already packed in a package", 409);
                }

                var workflowPartId = Convert.ToInt32(serial["workflow_part_id"]);
                var routeRows = await GetWorkflowRouteRowsForPartAsync(connection, workflowPartId);
                var selected = routeRows.FirstOrDefault(step =>
                    string.Equals(step["station_code"]?.ToString(), station["station_code"]?.ToString(), StringComparison.OrdinalIgnoreCase));
                if (selected is null)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("Logged-in Pack station is not in this serial route", 409);
                }

                var currentOrder = ResolveCurrentOrder(serial, routeRows);
                var selectedOrder = Convert.ToInt32(selected["station_order"]);
                var serialStatus = serial["serial_status"]?.ToString();
                if (selectedOrder < currentOrder || (selectedOrder == currentOrder && string.Equals(serialStatus, "Completed", StringComparison.OrdinalIgnoreCase)))
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("Station is already passed", 409);
                }

                var blockingStep = FindBlockingRequiredStep(routeRows, currentOrder, selectedOrder);
                if (blockingStep is not null)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage($"Previous station \"{GetStationDisplayName(blockingStep)}\" is not passed", 409);
                }

                var box = await GetOrCreateOpenWorkflowBoxAsync(connection, Convert.ToInt32(station["workflow_part_id"]), station["workflow_work_order_id"], loginId);
                var scannedQty = await ScalarAsync<int>(connection, "SELECT COUNT(*)::int FROM workflow_multibox_items WHERE box_id = @boxId", ("boxId", box["id"]));
                if (scannedQty >= boxQty)
                {
                    await ExecuteAsync(connection, "UPDATE workflow_multiboxes SET status = 'CLOSED', closed_at = COALESCE(closed_at, NOW()), updated_at = NOW() WHERE id = @boxId", ("boxId", box["id"]));
                    await transaction.CommitAsync();
                    return JsonMessage("Box Qty reached", 409);
                }

                await ExecuteAsync(
                    connection,
                    "INSERT INTO workflow_multibox_items (box_id, workflow_serial_id, added_by) VALUES (@boxId, @serialId, @loginId)",
                    ("boxId", box["id"]),
                    ("serialId", serial["id"]),
                    ("loginId", loginId));

                var nextStep = routeRows.FirstOrDefault(step => Convert.ToInt32(step["station_order"]) > selectedOrder);
                var nextStatus = nextStep is null ? "Completed" : "In Process";
                var nextStationCode = nextStep?["station_code"] ?? selected["station_code"];
                var nextStationOrder = nextStep?["station_order"] ?? selected["station_order"];

                await ExecuteAsync(
                    connection,
                    """
                    UPDATE workflow_serial_numbers
                    SET status = @status,
                        condition = 'Good',
                        current_station_code = @stationCode,
                        current_station_order = @stationOrder,
                        last_moved_at = NOW(),
                        updated_at = NOW()
                    WHERE id = @id
                    """,
                    ("status", nextStatus),
                    ("stationCode", nextStationCode),
                    ("stationOrder", nextStationOrder),
                    ("id", serial["id"]));

                await InsertWorkflowStationLogAsync(
                    connection,
                    serial,
                    selected,
                    "PASS",
                    "Operator multibox pack scan",
                    loginId,
                    serial["current_station_code"],
                    serial["current_station_order"],
                    nextStationCode,
                    nextStationOrder);

                scannedQty += 1;
                if (scannedQty >= boxQty)
                {
                    await ExecuteAsync(connection, "UPDATE workflow_multiboxes SET status = 'CLOSED', closed_at = NOW(), updated_at = NOW() WHERE id = @boxId", ("boxId", box["id"]));
                }
                else
                {
                    await ExecuteAsync(connection, "UPDATE workflow_multiboxes SET updated_at = NOW() WHERE id = @boxId", ("boxId", box["id"]));
                }

                await transaction.CommitAsync();
                var items = await GetWorkflowBoxItemsAsync(connection, box["id"]);
                return Results.Json(new
                {
                    message = scannedQty >= boxQty ? "Box completed" : "Serial added to box",
                    box_qty = boxQty,
                    scanned_qty = scannedQty,
                    remaining_qty = Math.Max(boxQty - scannedQty, 0),
                    box_no = box["box_no"],
                    is_closed = scannedQty >= boxQty,
                    items
                }, statusCode: 201);
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                await transaction.RollbackAsync();
                return JsonMessage("This serial number is already scanned into a box", 409);
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });

        app.MapGet("/api/operator/pallet/status", async (HttpRequest request) =>
        {
            var loginId = request.Query["loginId"].ToString().Trim();
            var workflowPartIdFilter = int.TryParse(request.Query["workflowPartId"].ToString(), out var parsedWorkflowPartId)
                ? parsedWorkflowPartId
                : (int?)null;
            if (string.IsNullOrWhiteSpace(loginId))
            {
                return JsonMessage("Login ID is required", 400);
            }

            await using var connection = await OpenConnectionAsync();
            await EnsureWorkflowSchemaAsync(connection);
            var station = await GetOperatorStationByLoginAsync(connection, loginId, workflowPartIdFilter);
            if (station is null)
            {
                return JsonMessage("Invalid station login ID", 401);
            }

            if (!IsPackStation(station["station_code"]?.ToString(), station["station_name"]?.ToString()))
            {
                return Results.Json(new { enabled = false });
            }

            var pallet = await GetOrCreateOpenWorkflowPalletAsync(connection, loginId);
            return Results.Json(await BuildPalletPayloadAsync(connection, pallet));
        });

        app.MapPost("/api/operator/pallet/scan", async (HttpContext context) =>
        {
            var payload = await ReadJsonBodyAsync(context.Request);
            var loginId = ReadString(payload, "loginId")?.Trim();
            var query = ReadString(payload, "query")?.Trim();
            var targetQty = ReadInt(payload, "targetQty");
            var workflowPartIdFilter = ReadInt(payload, "workflowPartId");
            if (string.IsNullOrWhiteSpace(loginId) || string.IsNullOrWhiteSpace(query))
            {
                return JsonMessage("Login ID and multibox number are required", 400);
            }

            if (targetQty is null || targetQty <= 0)
            {
                return JsonMessage("Pallet Qty is required", 400);
            }

            await using var connection = await OpenConnectionAsync();
            await EnsureWorkflowSchemaAsync(connection);
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                var station = await GetOperatorStationByLoginAsync(connection, loginId, workflowPartIdFilter);
                if (station is null)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("Invalid station login ID", 401);
                }

                if (!IsPackStation(station["station_code"]?.ToString(), station["station_name"]?.ToString()))
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("Pallet is available only for Pack station", 403);
                }

                var pallet = await GetOrCreateOpenWorkflowPalletAsync(connection, loginId);
                if (string.Equals(pallet["status"]?.ToString(), "CLOSED", StringComparison.OrdinalIgnoreCase))
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("Pallet is already closed", 409);
                }

                var scannedQty = await ScalarAsync<int>(connection, "SELECT COUNT(*)::int FROM workflow_pallet_items WHERE pallet_id = @palletId", ("palletId", pallet["id"]));
                if (scannedQty > 0 && Convert.ToInt32(pallet["target_qty"]) != targetQty.Value)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("Pallet Qty cannot be changed after scanning", 409);
                }

                await ExecuteAsync(connection, "UPDATE workflow_pallets SET target_qty = @targetQty, updated_at = NOW() WHERE id = @id", ("targetQty", targetQty.Value), ("id", pallet["id"]));
                pallet["target_qty"] = targetQty.Value;

                if (scannedQty >= targetQty.Value)
                {
                    await ExecuteAsync(connection, "UPDATE workflow_pallets SET status = 'CLOSED', closed_at = COALESCE(closed_at, NOW()), updated_at = NOW() WHERE id = @id", ("id", pallet["id"]));
                    await transaction.CommitAsync();
                    return JsonMessage("Pallet Qty reached", 409);
                }

                var boxes = await QueryRowsAsync(
                    connection,
                    "SELECT * FROM workflow_multiboxes WHERE UPPER(box_no) = UPPER(@query) AND status = 'CLOSED' LIMIT 1",
                    ("query", query));
                if (boxes.Count == 0)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("Closed multibox number not found", 404);
                }

                await ExecuteAsync(
                    connection,
                    "INSERT INTO workflow_pallet_items (pallet_id, box_id, added_by) VALUES (@palletId, @boxId, @loginId)",
                    ("palletId", pallet["id"]),
                    ("boxId", boxes[0]["id"]),
                    ("loginId", loginId));

                scannedQty += 1;
                if (scannedQty >= targetQty.Value)
                {
                    await ExecuteAsync(connection, "UPDATE workflow_pallets SET status = 'CLOSED', closed_at = NOW(), updated_at = NOW() WHERE id = @id", ("id", pallet["id"]));
                    pallet["status"] = "CLOSED";
                    pallet["closed_at"] = DateTime.UtcNow;
                }
                else
                {
                    await ExecuteAsync(connection, "UPDATE workflow_pallets SET updated_at = NOW() WHERE id = @id", ("id", pallet["id"]));
                }

                await transaction.CommitAsync();
                pallet = (await QueryRowsAsync(connection, "SELECT * FROM workflow_pallets WHERE id = @id", ("id", pallet["id"])))[0];
                var response = await BuildPalletPayloadAsync(connection, pallet);
                response["message"] = scannedQty >= targetQty.Value ? "Pallet completed" : "Multibox added to pallet";
                return Results.Json(response, statusCode: 201);
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                await transaction.RollbackAsync();
                return JsonMessage("This multibox is already scanned into a pallet", 409);
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });

        app.MapGet("/api/operator/shipment/status", async (HttpRequest request) =>
        {
            var loginId = request.Query["loginId"].ToString().Trim();
            var workflowPartIdFilter = int.TryParse(request.Query["workflowPartId"].ToString(), out var parsedWorkflowPartId)
                ? parsedWorkflowPartId
                : (int?)null;
            if (string.IsNullOrWhiteSpace(loginId))
            {
                return JsonMessage("Login ID is required", 400);
            }

            await using var connection = await OpenConnectionAsync();
            await EnsureWorkflowSchemaAsync(connection);
            var station = await GetOperatorStationByLoginAsync(connection, loginId, workflowPartIdFilter);
            if (station is null)
            {
                return JsonMessage("Invalid station login ID", 401);
            }

            if (!IsPackStation(station["station_code"]?.ToString(), station["station_name"]?.ToString()))
            {
                return Results.Json(new { enabled = false });
            }

            var shipment = await GetOrCreateOpenWorkflowShipmentAsync(connection, loginId);
            return Results.Json(await BuildShipmentPayloadAsync(connection, shipment));
        });

        app.MapPost("/api/operator/shipment/scan", async (HttpContext context) =>
        {
            var payload = await ReadJsonBodyAsync(context.Request);
            var loginId = ReadString(payload, "loginId")?.Trim();
            var query = ReadString(payload, "query")?.Trim();
            var targetQty = ReadInt(payload, "targetQty");
            var workflowPartIdFilter = ReadInt(payload, "workflowPartId");
            if (string.IsNullOrWhiteSpace(loginId) || string.IsNullOrWhiteSpace(query))
            {
                return JsonMessage("Login ID and pallet number are required", 400);
            }

            if (targetQty is null || targetQty <= 0)
            {
                return JsonMessage("Shipment Qty is required", 400);
            }

            await using var connection = await OpenConnectionAsync();
            await EnsureWorkflowSchemaAsync(connection);
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                var station = await GetOperatorStationByLoginAsync(connection, loginId, workflowPartIdFilter);
                if (station is null)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("Invalid station login ID", 401);
                }

                if (!IsPackStation(station["station_code"]?.ToString(), station["station_name"]?.ToString()))
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("Shipment is available only for Pack station", 403);
                }

                var shipment = await GetOrCreateOpenWorkflowShipmentAsync(connection, loginId);
                var scannedQty = await ScalarAsync<int>(connection, "SELECT COUNT(*)::int FROM workflow_shipment_items WHERE shipment_id = @shipmentId", ("shipmentId", shipment["id"]));
                if (scannedQty > 0 && Convert.ToInt32(shipment["target_qty"]) != targetQty.Value)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("Shipment Qty cannot be changed after scanning", 409);
                }

                await ExecuteAsync(connection, "UPDATE workflow_shipments SET target_qty = @targetQty, updated_at = NOW() WHERE id = @id", ("targetQty", targetQty.Value), ("id", shipment["id"]));
                shipment["target_qty"] = targetQty.Value;

                if (scannedQty >= targetQty.Value)
                {
                    await ExecuteAsync(connection, "UPDATE workflow_shipments SET status = 'CLOSED', closed_at = COALESCE(closed_at, NOW()), updated_at = NOW() WHERE id = @id", ("id", shipment["id"]));
                    await transaction.CommitAsync();
                    return JsonMessage("Shipment Qty reached", 409);
                }

                var pallets = await QueryRowsAsync(
                    connection,
                    "SELECT * FROM workflow_pallets WHERE UPPER(pallet_no) = UPPER(@query) AND status = 'CLOSED' LIMIT 1",
                    ("query", query));
                if (pallets.Count == 0)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("Closed pallet number not found", 404);
                }

                await ExecuteAsync(
                    connection,
                    "INSERT INTO workflow_shipment_items (shipment_id, pallet_id, added_by) VALUES (@shipmentId, @palletId, @loginId)",
                    ("shipmentId", shipment["id"]),
                    ("palletId", pallets[0]["id"]),
                    ("loginId", loginId));

                scannedQty += 1;
                if (scannedQty >= targetQty.Value)
                {
                    await ExecuteAsync(connection, "UPDATE workflow_shipments SET status = 'CLOSED', closed_at = NOW(), updated_at = NOW() WHERE id = @id", ("id", shipment["id"]));
                    shipment["status"] = "CLOSED";
                    shipment["closed_at"] = DateTime.UtcNow;
                }
                else
                {
                    await ExecuteAsync(connection, "UPDATE workflow_shipments SET updated_at = NOW() WHERE id = @id", ("id", shipment["id"]));
                }

                await transaction.CommitAsync();
                shipment = (await QueryRowsAsync(connection, "SELECT * FROM workflow_shipments WHERE id = @id", ("id", shipment["id"])))[0];
                var response = await BuildShipmentPayloadAsync(connection, shipment);
                response["message"] = scannedQty >= targetQty.Value ? "Shipment completed" : "Pallet added to shipment";
                return Results.Json(response, statusCode: 201);
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                await transaction.RollbackAsync();
                return JsonMessage("This pallet is already scanned into a shipment", 409);
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });

        app.MapGet("/api/operator/packaging/history", async (HttpRequest request) =>
        {
            var loginId = request.Query["loginId"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(loginId))
            {
                return JsonMessage("Login ID is required", 400);
            }

            await using var connection = await OpenConnectionAsync();
            await EnsureWorkflowSchemaAsync(connection);
            var rows = await GetWorkflowPackagingHistoryAsync(connection, loginId);
            return Results.Json(new { data = rows });
        });

        app.MapPost("/api/operator/pass", async (HttpContext context) =>
        {
            var payload = await ReadJsonBodyAsync(context.Request);
            var query = ReadString(payload, "query")?.Trim();
            var loginId = ReadString(payload, "loginId")?.Trim();
            var requestedWorkflowPartId = ReadInt(payload, "workflowPartId");
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(loginId))
            {
                return JsonMessage("Serial number and login ID are required", 400);
            }

            await using var connection = await OpenConnectionAsync();
            await EnsureWorkflowSchemaAsync(connection);
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                var operatorStation = await GetOperatorStationByLoginAsync(connection, loginId, requestedWorkflowPartId);
                if (operatorStation is null)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("Station login session is no longer assigned. Please login again", 401);
                }

                var serial = await GetWorkflowSerialByQueryAsync(connection, query);
                if (serial is null)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("SN/RSN not found", 404);
                }

                var workflowPartId = Convert.ToInt32(serial["workflow_part_id"]);
                if (Convert.ToInt32(operatorStation["workflow_part_id"]) != workflowPartId)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("This station login is not assigned to this serial number part number", 409);
                }

                var routeRows = await GetWorkflowRouteRowsForPartAsync(connection, workflowPartId);
                var selected = routeRows.FirstOrDefault(step =>
                    string.Equals(step["station_code"]?.ToString(), operatorStation["station_code"]?.ToString(), StringComparison.OrdinalIgnoreCase));
                if (selected is null)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("Logged-in station is not in this serial route", 409);
                }

                var currentOrder = ResolveCurrentOrder(serial, routeRows);
                var selectedOrder = Convert.ToInt32(selected["station_order"]);
                var serialStatus = serial["serial_status"]?.ToString();
                if (selectedOrder < currentOrder || (selectedOrder == currentOrder && string.Equals(serialStatus, "Completed", StringComparison.OrdinalIgnoreCase)))
                {
                    var repairRedirectMessage = await ResolveRepairRedirectMessageAsync(connection, serial, workflowPartId, routeRows, selected, query);
                    if (repairRedirectMessage is not null)
                    {
                        await InsertWorkflowStationLogAsync(
                            connection,
                            serial,
                            selected,
                            "NOT_PASS",
                            repairRedirectMessage,
                            loginId,
                            serial["current_station_code"],
                            serial["current_station_order"],
                            serial["current_station_code"],
                            serial["current_station_order"]);
                        await transaction.CommitAsync();
                        return JsonMessage(repairRedirectMessage, 409);
                    }

                    await InsertWorkflowStationLogAsync(
                        connection,
                        serial,
                        selected,
                        "NOT_PASS",
                        "Station is already passed",
                        loginId,
                        serial["current_station_code"],
                        serial["current_station_order"],
                        serial["current_station_code"],
                        serial["current_station_order"]);
                    await transaction.CommitAsync();
                    return JsonMessage("Station is already passed", 409);
                }

                var pendingRepairStep = FindPendingRepairStep(serial, routeRows, selected);
                if (pendingRepairStep is not null)
                {
                    var stationName = GetStationDisplayName(pendingRepairStep);
                    await InsertWorkflowStationLogAsync(
                        connection,
                        serial,
                        selected,
                        "NOT_PASS",
                        $"Repair station \"{stationName}\" is not passed",
                        loginId,
                        serial["current_station_code"],
                        serial["current_station_order"],
                        serial["current_station_code"],
                        serial["current_station_order"]);
                    await transaction.CommitAsync();
                    return JsonMessage($"Repair station \"{stationName}\" is not passed. Please pass repair station before continuing.", 409);
                }

                var blockingStep = FindBlockingRequiredStep(routeRows, currentOrder, selectedOrder);
                if (blockingStep is not null)
                {
                    var stationName = GetStationDisplayName(blockingStep);
                    await InsertWorkflowStationLogAsync(
                        connection,
                        serial,
                        selected,
                        "NOT_PASS",
                        $"Previous station \"{stationName}\" is not passed",
                        loginId,
                        serial["current_station_code"],
                        serial["current_station_order"],
                        serial["current_station_code"],
                        serial["current_station_order"]);
                    await transaction.CommitAsync();
                    return JsonMessage($"Previous station \"{stationName}\" is not passed", 409);
                }

                var failedPreviousStep = await FindLatestFailedPreviousStepAsync(connection, serial["id"]!, workflowPartId, routeRows, selected);
                if (failedPreviousStep is not null)
                {
                    var stationName = GetStationDisplayName(failedPreviousStep);
                    await InsertWorkflowStationLogAsync(
                        connection,
                        serial,
                        selected,
                        "NOT_PASS",
                        $"Previous station \"{stationName}\" is failed",
                        loginId,
                        serial["current_station_code"],
                        serial["current_station_order"],
                        serial["current_station_code"],
                        serial["current_station_order"]);
                    await transaction.CommitAsync();
                    return JsonMessage($"Previous station \"{stationName}\" is failed. Please pass that station before continuing.", 409);
                }

                var samplingDecision = await ResolveSamplingDecisionAsync(connection, workflowPartId, selected, serial);
                if (samplingDecision.IsEnabled && !samplingDecision.IsRequired)
                {
                    var nextStepForSampling = routeRows.FirstOrDefault(step => Convert.ToInt32(step["station_order"]) > selectedOrder);
                    var nextStatusForSampling = nextStepForSampling is null ? "Completed" : "In Process";
                    var nextStationCodeForSampling = nextStepForSampling?["station_code"] ?? selected["station_code"];
                    var nextStationOrderForSampling = nextStepForSampling?["station_order"] ?? selected["station_order"];
                    var samplingRemark = $"Sampling skipped - auto passed ({samplingDecision.Reason})";

                    await ExecuteAsync(
                        connection,
                        """
                        UPDATE workflow_serial_numbers
                        SET status = @status,
                            condition = 'Good',
                            current_station_code = @stationCode,
                            current_station_order = @stationOrder,
                            last_moved_at = NOW(),
                            updated_at = NOW()
                        WHERE id = @id
                        """,
                        ("status", nextStatusForSampling),
                        ("stationCode", nextStationCodeForSampling),
                        ("stationOrder", nextStationOrderForSampling),
                        ("id", serial["id"]));

                    await InsertWorkflowStationLogAsync(
                        connection,
                        serial,
                        selected,
                        "PASS",
                        samplingRemark,
                        loginId,
                        serial["current_station_code"],
                        serial["current_station_order"],
                        nextStationCodeForSampling,
                        nextStationOrderForSampling);

                    await MirrorWorkflowPassToLegacyTraceAsync(
                        connection,
                        serial,
                        selected,
                        "PASS",
                        samplingRemark,
                        loginId,
                        serial["current_station_code"],
                        serial["current_station_order"],
                        nextStationCodeForSampling,
                        nextStationOrderForSampling,
                        nextStatusForSampling);

                    await transaction.CommitAsync();
                    return Results.Json(new
                    {
                        message = "SN not selected for sampling. Station auto-passed.",
                        station_code = selected["station_code"],
                        status = "Sampling Skipped",
                        sampling = samplingDecision
                    });
                }

                var bomStatus = await BuildWorkflowBomBindingStatusAsync(connection, serial, selected);
                if (bomStatus.RequiresBinding && bomStatus.Remaining > 0)
                {
                    await InsertWorkflowStationLogAsync(
                        connection,
                        serial,
                        selected,
                        "NOT_PASS",
                        $"Scan required BOM child serials before passing this station. Remaining: {bomStatus.Remaining}",
                        loginId,
                        serial["current_station_code"],
                        serial["current_station_order"],
                        serial["current_station_code"],
                        serial["current_station_order"]);
                    await transaction.CommitAsync();
                    return Results.Json(new
                    {
                        message = $"Scan required BOM child serials before passing this station. Remaining: {bomStatus.Remaining}",
                        assembly = bomStatus.Payload
                    }, statusCode: 409);
                }

                var repairReturnStep = await FindRepairReturnStepAsync(connection, serial["id"]!, workflowPartId, routeRows, selected);
                var nextStep = repairReturnStep
                    ?? routeRows.FirstOrDefault(step => Convert.ToInt32(step["station_order"]) > selectedOrder);
                var nextStatus = nextStep is null ? "Completed" : "In Process";
                var nextStationCode = nextStep?["station_code"] ?? selected["station_code"];
                var nextStationOrder = nextStep?["station_order"] ?? selected["station_order"];
                var passRemark = repairReturnStep is null
                    ? "Operator station pass"
                    : $"Repair station pass - return to {GetStationDisplayName(repairReturnStep)}";

                await ExecuteAsync(
                    connection,
                    """
                    UPDATE workflow_serial_numbers
                    SET status = @status,
                        condition = 'Good',
                        current_station_code = @stationCode,
                        current_station_order = @stationOrder,
                        last_moved_at = NOW(),
                        updated_at = NOW()
                    WHERE id = @id
                    """,
                    ("status", nextStatus),
                    ("stationCode", nextStationCode),
                    ("stationOrder", nextStationOrder),
                    ("id", serial["id"]));

                await InsertWorkflowStationLogAsync(
                    connection,
                    serial,
                    selected,
                    "PASS",
                    passRemark,
                    loginId,
                    serial["current_station_code"],
                    serial["current_station_order"],
                    nextStationCode,
                    nextStationOrder);

                await MirrorWorkflowPassToLegacyTraceAsync(
                    connection,
                    serial,
                    selected,
                    "PASS",
                    passRemark,
                    loginId,
                    serial["current_station_code"],
                    serial["current_station_order"],
                    nextStationCode,
                    nextStationOrder,
                    nextStatus);

                var labelPrinting = await GetWorkflowStationLabelPrintingConfigAsync(
                    connection,
                    workflowPartId,
                    selected["station_code"]?.ToString());

                await transaction.CommitAsync();
                await TryPrintWorkflowStationLabelAsync(connection, serial, selected, labelPrinting);
                return Results.Json(new
                {
                    message = repairReturnStep is null
                        ? "Station passed successfully"
                        : $"Repair station passed successfully. Please pass {GetStationDisplayName(repairReturnStep)} again before continuing.",
                    station_code = selected["station_code"],
                    status = "Passed",
                    label_printing = labelPrinting
                });
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });
    }

    private static void MapExternal(WebApplication app)
    {
        app.MapGet("/api/ws", async (HttpRequest request) =>
        {
            var phpClass = request.Query["php_class"].ToString().Trim();
            var func = request.Query["func"].ToString().Trim().ToLowerInvariant();

            if (!string.Equals(phpClass, "ws_json", StringComparison.OrdinalIgnoreCase))
            {
                return LegacyWsSimpleFail(func, "php_class=ws_json is required", 400);
            }

            return func switch
            {
                "snstatus" => await HandleLegacyWsSnStatusAsync(request),
                "insertresult" => await HandleLegacyWsInsertResultAsync(request),
                "snhistory" => await HandleLegacyWsSnHistoryAsync(request),
                "routeback" => LegacyWsSimpleFail(func, "routeback is routed but not implemented yet", 501),
                "" => LegacyWsSimpleFail(func, "func is required", 400),
                _ => LegacyWsSimpleFail(func, $"Unsupported func \"{func}\"", 400)
            };
        })
        .WithName("LegacyMesWs")
        .WithTags("External")
        .WithSummary("Legacy QMS/MS3-style MES integration endpoint with func routing.");

        app.MapGet("/api/external/sn-status", async (HttpRequest request) =>
        {
            var rsn = request.Query["rsn"].ToString().Trim();
            var userId = request.Query["userId"].ToString().Trim();
            var password = request.Query["password"].ToString();
            var stationCode = request.Query["stationCode"].ToString().Trim();
            var chipId = request.Query["chipId"].ToString().Trim();
            var imes = request.Query["imes"].ToString().Trim();

            if (string.IsNullOrWhiteSpace(rsn))
            {
                return ExternalSnStatusError("rsn is required", 400);
            }

            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(password))
            {
                return ExternalSnStatusError("userId and password are required", 400);
            }

            await using var connection = await OpenConnectionAsync();
            await EnsureWorkflowSchemaAsync(connection);
            await EnsureWorkflowStationLoginsTableAsync(connection);
            await EnsureSerialExternalValuesTableAsync(connection);

            var hasValidOperatorCredentials = await HasValidOperatorCredentialsAsync(connection, userId, password);
            if (!hasValidOperatorCredentials)
            {
                return ExternalSnStatusError("Invalid operator credentials", 401);
            }

            var serial = await GetWorkflowSerialByQueryAsync(connection, rsn);
            if (serial is null)
            {
                return ExternalSnStatusError("Serial Number not found", 404);
            }

            if (serial["workflow_work_order_id"] is null || string.IsNullOrWhiteSpace(serial["wo"]?.ToString()))
            {
                return ExternalSnStatusError("Work Order not found", 404, serial);
            }

            var workflowPartId = Convert.ToInt32(serial["workflow_part_id"]);
            var routeRows = await GetWorkflowRouteRowsForPartAsync(connection, workflowPartId);
            if (routeRows.Count == 0)
            {
                return ExternalSnStatusError("Routing not found", 404, serial);
            }

            var currentOrder = ResolveCurrentOrder(serial, routeRows);
            var requestedStation = ResolveExternalRequestedStation(serial, routeRows, stationCode, currentOrder);
            if (requestedStation is null)
            {
                return ExternalSnStatusError("Routing not found", 404, serial);
            }

            var credentialStation = await GetOperatorStationByCredentialsAsync(
                connection,
                userId,
                password,
                workflowPartId,
                requestedStation["station_code"]?.ToString());
            if (credentialStation is null)
            {
                return ExternalSnStatusError("Invalid operator credentials", 401, serial);
            }

            var history = await BuildExternalSnHistoryAsync(connection, serial, routeRows);
            var stationHistory = history
                .Where(row => string.Equals(row["stationCode"]?.ToString(), requestedStation["station_code"]?.ToString(), StringComparison.OrdinalIgnoreCase))
                .ToList();
            var requestedStationCode = requestedStation["station_code"]?.ToString() ?? string.Empty;
            var latestRequestedResult = stationHistory.FirstOrDefault(row =>
                string.Equals(row["stationCode"]?.ToString(), requestedStationCode, StringComparison.OrdinalIgnoreCase));
            var requestedStationPassed = latestRequestedResult is not null &&
                string.Equals(latestRequestedResult["result"]?.ToString(), "PASS", StringComparison.OrdinalIgnoreCase);

            if (latestRequestedResult is not null &&
                string.Equals(latestRequestedResult["result"]?.ToString(), "FAIL", StringComparison.OrdinalIgnoreCase))
            {
                return ExternalSnStatusResponse(false, "FAIL", "Station already failed", serial, requestedStation, stationHistory, null, 409);
            }

            var selectedOrder = Convert.ToInt32(requestedStation["station_order"]);
            var pendingRepairStep = FindPendingRepairStep(serial, routeRows, requestedStation);
            if (pendingRepairStep is not null)
            {
                var stationName = GetStationDisplayName(pendingRepairStep);
                return ExternalSnStatusResponse(false, "FAIL", $"Repair station \"{stationName}\" is not passed. Please pass repair station before continuing.", serial, requestedStation, stationHistory, null, 409);
            }

            var blockingStep = FindBlockingRequiredStep(routeRows, currentOrder, selectedOrder);
            if (blockingStep is not null)
            {
                var stationName = GetStationDisplayName(blockingStep);
                return ExternalSnStatusResponse(false, "FAIL", $"Previous station \"{stationName}\" is not passed", serial, requestedStation, stationHistory, null, 409);
            }

            var hasExternalValues = !string.IsNullOrWhiteSpace(chipId) || !string.IsNullOrWhiteSpace(imes);
            if (!requestedStationPassed)
            {
                var reason = hasExternalValues
                    ? "Station is not passed. External values cannot be saved."
                    : "Station is not passed";
                return ExternalSnStatusResponse(false, "FAIL", reason, serial, requestedStation, stationHistory, null, 409);
            }

            if (hasExternalValues)
            {
                await UpsertSerialExternalValuesAsync(connection, serial, requestedStation, userId, chipId, imes);
            }

            var externalValues = await GetSerialExternalValuesAsync(connection, serial["id"]!);
            return ExternalSnStatusResponse(true, "PASS", string.Empty, serial, requestedStation, stationHistory, externalValues);
        })
        .WithName("GetExternalSnStatus")
        .WithTags("External")
        .WithSummary("Validates K9Operator station credentials and returns SN routing status.");
    }

                                    private static void MapWorkflow(WebApplication app)
    {
        app.MapGet("/api/workflow/by-pn", async (HttpRequest request) =>
        {
            var pn = request.Query["pn"].ToString().Trim();
            var wo = request.Query["wo"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(pn))
            {
                return JsonMessage("pn is required", 400);
            }

            await using var connection = await OpenConnectionAsync();
            await EnsureWorkflowSchemaAsync(connection);
            var snapshot = await GetWorkflowSnapshotAsync(connection, pn, wo);
            return snapshot is null ? JsonMessage("Workflow not found", 404) : Results.Json(snapshot);
        });

    }

            private static void MapTraceability(WebApplication app)
    {
        app.MapGet("/api/traceability/schema/verify", async () =>
        {
            await using var connection = await OpenConnectionAsync();
            await EnsureSerialTrackingSchemaAsync(connection);
            var hasSerialNumbers = await ScalarAsync<bool>(
                connection,
                """
                SELECT EXISTS (
                  SELECT 1
                  FROM information_schema.tables
                  WHERE table_schema = 'public'
                    AND table_name = 'serial_numbers'
                )
                """);
            return Results.Json(new { serial_numbers_exists = hasSerialNumbers });
        });

        app.MapGet("/api/traceability/search", async (HttpRequest request) =>
        {
            var query = request.Query["query"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                return JsonMessage("Search query is required", 400);
            }

            await using var connection = await OpenConnectionAsync();
            await EnsureSerialTrackingSchemaAsync(connection);
            await EnsureWorkflowSchemaAsync(connection);
            var workflowSerial = await GetWorkflowSerialByQueryAsync(connection, query);
            if (workflowSerial is not null)
            {
                return Results.Json(await BuildWorkflowTracePayloadAsync(connection, query, workflowSerial));
            }

            var serial = await GetSerialByQueryAsync(connection, query);
            if (serial is not null)
            {
                return Results.Json(await BuildTracePayloadAsync(connection, query, serial));
            }

            return JsonMessage("SN/RSN not found", 404);
        });

        app.MapPost("/api/traceability/pass-fail", async (HttpContext context) =>
        {
            var payload = await ReadJsonBodyAsync(context.Request);
            var query = ReadString(payload, "query")?.Trim();
            var stationCode = ReadString(payload, "station_code")?.Trim();
            var result = ReadString(payload, "result")?.Trim().ToUpperInvariant();
            var remark = ReadString(payload, "remark")?.Trim();
            var changedBy = ReadString(payload, "changed_by")?.Trim() ?? "system";
            var stationLength = ReadString(payload, "station_length")?.Trim();
            var pcName = ReadString(payload, "pc_name")?.Trim() ?? "WEB-CLIENT";
            var additionalInfo = ReadString(payload, "additional_info")?.Trim() ?? (result == "PASS" ? "Auto Pass Result" : "Auto Fail Result");

            if (string.IsNullOrWhiteSpace(query))
            {
                return JsonMessage("SN or RSN is required", 400);
            }

            if (string.IsNullOrWhiteSpace(stationCode))
            {
                return JsonMessage("Station code is required", 400);
            }

            if (result is not ("PASS" or "FAIL"))
            {
                return JsonMessage("result must be PASS or FAIL", 400);
            }

            await using var connection = await OpenConnectionAsync();
            await EnsureSerialTrackingSchemaAsync(connection);
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                var serial = await GetSerialByQueryAsync(connection, query);
                if (serial is null)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("SN/RSN not found", 404);
                }

                var routeRows = await GetRouteRowsForItemAsync(connection, Convert.ToInt32(serial["item_id"]));
                if (routeRows.Count == 0)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("No route configured for this part number", 400);
                }

                var selected = routeRows.FirstOrDefault(step => string.Equals(step["station_code"]?.ToString(), stationCode, StringComparison.OrdinalIgnoreCase));
                if (selected is null)
                {
                    await transaction.RollbackAsync();
                    return JsonMessage("Selected station is not in this part route", 400);
                }

                var currentOrder = ResolveCurrentOrder(serial, routeRows);
                var current = routeRows.FirstOrDefault(step => Convert.ToInt32(step["station_order"]) == currentOrder) ?? routeRows[0];
                if (Convert.ToInt32(selected["station_order"]) != Convert.ToInt32(current["station_order"]))
                {
                    await transaction.RollbackAsync();
                    return JsonMessage($"Current station is {current["station_code"]}. Please select current station first.", 409);
                }

                var nextStep = result == "PASS"
                    ? routeRows.FirstOrDefault(step => Convert.ToInt32(step["station_order"]) > Convert.ToInt32(current["station_order"]))
                    : current;
                var nextStatus = result == "PASS" ? (nextStep is null ? "Completed" : "In Process") : "Failed";
                var nextCondition = result == "PASS" ? "Good" : "NG";
                var nextStationCode = result == "PASS" ? nextStep?["station_code"] : current["station_code"];
                var nextStationOrder = result == "PASS" ? nextStep?["station_order"] : current["station_order"];

                await ExecuteAsync(
                    connection,
                    """
                    UPDATE serial_numbers
                    SET status = @status,
                        condition = @condition,
                        current_station_code = @stationCode,
                        current_station_order = @stationOrder,
                        last_moved_at = NOW(),
                        updated_at = NOW()
                    WHERE id = @id
                    """,
                    ("status", nextStatus),
                    ("condition", nextCondition),
                    ("stationCode", nextStationCode),
                    ("stationOrder", nextStationOrder),
                    ("id", serial["id"]));

                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO serial_station_logs
                      (serial_id, item_id, work_order_id, station_code, station_name, action_result, remark, changed_by,
                       before_station_code, before_station_order, after_station_code, after_station_order,
                       station_length, pc_name, additional_info)
                    VALUES
                      (@serialId, @itemId, @workOrderId, @stationCode, @stationName, @result, @remark, @changedBy,
                       @beforeCode, @beforeOrder, @afterCode, @afterOrder, @stationLength, @pcName, @additionalInfo)
                    """,
                    ("serialId", serial["id"]),
                    ("itemId", serial["item_id"]),
                    ("workOrderId", serial["work_order_id"]),
                    ("stationCode", current["station_code"]),
                    ("stationName", current["station_name"]),
                    ("result", result),
                    ("remark", ToDbNullable(remark)),
                    ("changedBy", changedBy),
                    ("beforeCode", serial["current_station_code"]),
                    ("beforeOrder", serial["current_station_order"]),
                    ("afterCode", nextStationCode),
                    ("afterOrder", nextStationOrder),
                    ("stationLength", ToDbNullable(stationLength)),
                    ("pcName", pcName),
                    ("additionalInfo", additionalInfo));

                var refreshed = await GetSerialByQueryAsync(connection, serial["rsn"]!.ToString()!);
                var trace = await BuildTracePayloadAsync(connection, serial["rsn"]!.ToString()!, refreshed!);
                await transaction.CommitAsync();
                return Results.Json(new { message = result == "PASS" ? "PASS submitted successfully" : "FAIL submitted successfully", action = result, data = trace });
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });
    }

                                                                private static async Task<Dictionary<string, object?>?> GetSerialByQueryAsync(NpgsqlConnection connection, string query)
    {
        var rows = await QueryRowsAsync(
            connection,
            """
            SELECT snr.id, snr.sn, snr.rsn, snr.generated_index, snr.status AS serial_status, snr.condition,
                   snr.current_station_code, snr.current_station_order, snr.last_moved_at,
                   snr.created_at, snr.updated_at,
                   wo.id AS work_order_id, wo.wo, wo.status AS wo_status, wo.qty AS wo_qty, wo.balance AS wo_balance,
                   i.id AS item_id, i.pn, i.description AS item_description,
                   ir.revision, s.name AS site_name,
                   pl.code AS product_line_code, pl.description AS product_line_name,
                   '' AS plant
            FROM serial_numbers snr
            JOIN work_orders wo ON wo.id = snr.work_order_id
            JOIN items i ON i.id = snr.item_id
            LEFT JOIN item_revisions ir ON ir.id = snr.item_revision_id
            LEFT JOIN sites s ON s.id = snr.site_id
            LEFT JOIN product_lines pl ON pl.id = i.product_line_id
            WHERE UPPER(snr.sn) = UPPER(@query)
               OR UPPER(snr.rsn) = UPPER(@query)
            ORDER BY snr.created_at DESC
            LIMIT 1
            """,
            ("query", query));
        return rows.Count == 0 ? null : rows[0];
    }

    private static async Task<Dictionary<string, object?>?> GetWorkflowSerialByQueryAsync(NpgsqlConnection connection, string query)
    {
        var rows = await QueryRowsAsync(
            connection,
            """
            SELECT snr.id, snr.sn, snr.rsn, snr.generated_index, snr.status AS serial_status, snr.condition,
                   snr.current_station_code, snr.current_station_order, snr.last_moved_at,
                   snr.created_at, snr.updated_at,
                   w.id AS workflow_work_order_id, w.wo, w.status AS wo_status, w.qty AS wo_qty,
                   GREATEST(COALESCE(w.qty, 0) - (
                     SELECT COUNT(*)::int
                     FROM workflow_serial_numbers generated
                     WHERE generated.workflow_work_order_id = w.id
                   ), 0) AS wo_balance,
                   p.id AS workflow_part_id, p.pn, p.description AS item_description,
                   COALESCE(w.plant, '') AS plant,
                   COALESCE(w.lot, '') AS lot,
                   COALESCE(w.revision, '-') AS revision,
                   COALESCE(w.site_name, '-') AS site_name,
                   COALESCE(p.item_type, '-') AS product_line_name
            FROM workflow_serial_numbers snr
            JOIN workflow_work_orders w ON w.id = snr.workflow_work_order_id
            JOIN workflow_part_numbers p ON p.id = snr.workflow_part_id
            WHERE UPPER(snr.sn) = UPPER(@query)
               OR UPPER(snr.rsn) = UPPER(@query)
            ORDER BY snr.created_at DESC
            LIMIT 1
            """,
            ("query", query));
        return rows.Count == 0 ? null : rows[0];
    }

    private static async Task<Dictionary<string, object?>?> GetOperatorStationAsync(NpgsqlConnection connection, string loginId)
    {
        var rows = await QueryRowsAsync(
            connection,
            """
            SELECT
              r.id,
              r.station_code,
              r.station_name,
              r.station_order,
              r.workflow_part_id,
              r.station_login_id,
              p.box_qty,
              w.id AS workflow_work_order_id
            FROM workflow_routing_steps r
            JOIN workflow_part_numbers p ON p.id = r.workflow_part_id
            LEFT JOIN workflow_work_orders w ON w.workflow_part_id = p.id
            WHERE UPPER(r.station_login_id) = UPPER(@loginId)
            ORDER BY w.updated_at DESC NULLS LAST, r.updated_at DESC, r.id DESC
            LIMIT 1
            """,
            ("loginId", loginId));

        return rows.Count == 0 ? null : rows[0];
    }

    private static async Task<Dictionary<string, object?>> GetOrCreateOpenWorkflowBoxAsync(
        NpgsqlConnection connection,
        int workflowPartId,
        object? workflowWorkOrderId,
        string loginId)
    {
        var existing = await QueryRowsAsync(
            connection,
            """
            SELECT *
            FROM workflow_multiboxes
            WHERE workflow_part_id = @workflowPartId
              AND status = 'OPEN'
              AND (
                (@workflowWorkOrderId::integer IS NULL AND workflow_work_order_id IS NULL)
                OR workflow_work_order_id = @workflowWorkOrderId::integer
              )
            ORDER BY created_at DESC, id DESC
            LIMIT 1
            """,
            ("workflowPartId", workflowPartId),
            ("workflowWorkOrderId", ToDbNullable(workflowWorkOrderId)));

        if (existing.Count > 0)
        {
            return existing[0];
        }

        var boxNo = $"MBX-{DateTime.Now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
        var created = await QueryRowsAsync(
            connection,
            """
            INSERT INTO workflow_multiboxes
              (box_no, workflow_part_id, workflow_work_order_id, status, created_by, updated_at)
            VALUES
              (@boxNo, @workflowPartId, @workflowWorkOrderId, 'OPEN', @loginId, NOW())
            RETURNING *
            """,
            ("boxNo", boxNo),
            ("workflowPartId", workflowPartId),
            ("workflowWorkOrderId", ToDbNullable(workflowWorkOrderId)),
            ("loginId", loginId));

        return created[0];
    }

    private static async Task<List<Dictionary<string, object?>>> GetWorkflowBoxItemsAsync(NpgsqlConnection connection, object? boxId)
    {
        return await QueryRowsAsync(
            connection,
            """
            SELECT
              ROW_NUMBER() OVER (ORDER BY mbi.added_at ASC, mbi.id ASC)::int AS seq,
              snr.sn,
              snr.rsn,
              snr.status,
              mbi.added_by,
              mbi.added_at
            FROM workflow_multibox_items mbi
            JOIN workflow_serial_numbers snr ON snr.id = mbi.workflow_serial_id
            WHERE mbi.box_id = @boxId
            ORDER BY mbi.added_at ASC, mbi.id ASC
            """,
            ("boxId", boxId));
    }

    private static async Task<Dictionary<string, object?>> GetOrCreateOpenWorkflowPalletAsync(NpgsqlConnection connection, string loginId)
    {
        var existing = await QueryRowsAsync(
            connection,
            """
            SELECT *
            FROM workflow_pallets
            WHERE status = 'OPEN'
              AND created_by = @loginId
            ORDER BY created_at DESC, id DESC
            LIMIT 1
            """,
            ("loginId", loginId));

        if (existing.Count > 0)
        {
            return existing[0];
        }

        var palletNo = $"PLT-{DateTime.Now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
        var created = await QueryRowsAsync(
            connection,
            """
            INSERT INTO workflow_pallets
              (pallet_no, target_qty, status, created_by, updated_at)
            VALUES
              (@palletNo, 0, 'OPEN', @loginId, NOW())
            RETURNING *
            """,
            ("palletNo", palletNo),
            ("loginId", loginId));

        return created[0];
    }

    private static async Task<Dictionary<string, object?>> GetOrCreateOpenWorkflowShipmentAsync(NpgsqlConnection connection, string loginId)
    {
        var existing = await QueryRowsAsync(
            connection,
            """
            SELECT *
            FROM workflow_shipments
            WHERE status = 'OPEN'
              AND created_by = @loginId
            ORDER BY created_at DESC, id DESC
            LIMIT 1
            """,
            ("loginId", loginId));

        if (existing.Count > 0)
        {
            return existing[0];
        }

        var shipmentNo = $"SHP-{DateTime.Now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
        var created = await QueryRowsAsync(
            connection,
            """
            INSERT INTO workflow_shipments
              (shipment_no, target_qty, status, created_by, updated_at)
            VALUES
              (@shipmentNo, 0, 'OPEN', @loginId, NOW())
            RETURNING *
            """,
            ("shipmentNo", shipmentNo),
            ("loginId", loginId));

        return created[0];
    }

    private static async Task<Dictionary<string, object?>> BuildPalletPayloadAsync(NpgsqlConnection connection, Dictionary<string, object?> pallet)
    {
        var scannedQty = await ScalarAsync<int>(connection, "SELECT COUNT(*)::int FROM workflow_pallet_items WHERE pallet_id = @palletId", ("palletId", pallet["id"]));
        var targetQty = pallet["target_qty"] is null ? 0 : Convert.ToInt32(pallet["target_qty"]);
        return new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["id"] = pallet["id"],
            ["code"] = pallet["pallet_no"],
            ["target_qty"] = targetQty,
            ["scanned_qty"] = scannedQty,
            ["remaining_qty"] = targetQty <= 0 ? null : Math.Max(targetQty - scannedQty, 0),
            ["is_closed"] = string.Equals(pallet["status"]?.ToString(), "CLOSED", StringComparison.OrdinalIgnoreCase),
            ["items"] = await GetWorkflowPalletItemsAsync(connection, pallet["id"])
        };
    }

    private static async Task<Dictionary<string, object?>> BuildShipmentPayloadAsync(NpgsqlConnection connection, Dictionary<string, object?> shipment)
    {
        var scannedQty = await ScalarAsync<int>(connection, "SELECT COUNT(*)::int FROM workflow_shipment_items WHERE shipment_id = @shipmentId", ("shipmentId", shipment["id"]));
        var targetQty = shipment["target_qty"] is null ? 0 : Convert.ToInt32(shipment["target_qty"]);
        return new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["id"] = shipment["id"],
            ["code"] = shipment["shipment_no"],
            ["target_qty"] = targetQty,
            ["scanned_qty"] = scannedQty,
            ["remaining_qty"] = targetQty <= 0 ? null : Math.Max(targetQty - scannedQty, 0),
            ["is_closed"] = string.Equals(shipment["status"]?.ToString(), "CLOSED", StringComparison.OrdinalIgnoreCase),
            ["items"] = await GetWorkflowShipmentItemsAsync(connection, shipment["id"])
        };
    }

    private static async Task<List<Dictionary<string, object?>>> GetWorkflowPalletItemsAsync(NpgsqlConnection connection, object? palletId)
    {
        return await QueryRowsAsync(
            connection,
            """
            SELECT
              ROW_NUMBER() OVER (ORDER BY pi.added_at ASC, pi.id ASC)::int AS seq,
              b.id,
              b.box_no AS code,
              b.status,
              (SELECT COUNT(*) FROM workflow_multibox_items mbi WHERE mbi.box_id = b.id)::int AS item_count,
              pi.added_by,
              pi.added_at
            FROM workflow_pallet_items pi
            JOIN workflow_multiboxes b ON b.id = pi.box_id
            WHERE pi.pallet_id = @palletId
            ORDER BY pi.added_at ASC, pi.id ASC
            """,
            ("palletId", palletId));
    }

    private static async Task<List<Dictionary<string, object?>>> GetWorkflowShipmentItemsAsync(NpgsqlConnection connection, object? shipmentId)
    {
        return await QueryRowsAsync(
            connection,
            """
            SELECT
              ROW_NUMBER() OVER (ORDER BY si.added_at ASC, si.id ASC)::int AS seq,
              p.id,
              p.pallet_no AS code,
              p.status,
              (SELECT COUNT(*) FROM workflow_pallet_items pi WHERE pi.pallet_id = p.id)::int AS item_count,
              si.added_by,
              si.added_at
            FROM workflow_shipment_items si
            JOIN workflow_pallets p ON p.id = si.pallet_id
            WHERE si.shipment_id = @shipmentId
            ORDER BY si.added_at ASC, si.id ASC
            """,
            ("shipmentId", shipmentId));
    }

    private static async Task<List<Dictionary<string, object?>>> GetWorkflowPackagingHistoryAsync(NpgsqlConnection connection, string loginId)
    {
        return await QueryRowsAsync(
            connection,
            """
            SELECT *
            FROM (
              SELECT
                b.id,
                b.box_no AS code,
                'Multibox' AS type,
                NULL::integer AS target_qty,
                (SELECT COUNT(*) FROM workflow_multibox_items mbi WHERE mbi.box_id = b.id)::int AS item_count,
                b.status,
                b.created_by,
                b.created_at,
                b.closed_at
              FROM workflow_multiboxes b
              WHERE b.created_by = @loginId

              UNION ALL

              SELECT
                p.id,
                p.pallet_no AS code,
                'Pallet' AS type,
                p.target_qty,
                (SELECT COUNT(*) FROM workflow_pallet_items pi WHERE pi.pallet_id = p.id)::int AS item_count,
                p.status,
                p.created_by,
                p.created_at,
                p.closed_at
              FROM workflow_pallets p
              WHERE p.created_by = @loginId

              UNION ALL

              SELECT
                s.id,
                s.shipment_no AS code,
                'Shipment' AS type,
                s.target_qty,
                (SELECT COUNT(*) FROM workflow_shipment_items si WHERE si.shipment_id = s.id)::int AS item_count,
                s.status,
                s.created_by,
                s.created_at,
                s.closed_at
              FROM workflow_shipments s
              WHERE s.created_by = @loginId
            ) history
            ORDER BY created_at DESC, id DESC
            LIMIT 100
            """,
            ("loginId", loginId));
    }

    private static async Task<List<Dictionary<string, object?>>> GetRouteRowsForItemAsync(NpgsqlConnection connection, int itemId)
    {
        await EnsureRoutingStepLoginColumnsAsync(connection);

        return await QueryRowsAsync(
            connection,
            """
            SELECT station_order, station_code, station_name, sample_mode, report_mode,
                   station_login_id, station_login_password, station_ip, printer_ip
            FROM item_routing_steps
            WHERE item_id = @itemId
            ORDER BY station_order ASC, id ASC
            """,
            ("itemId", itemId));
    }

    private static async Task<List<Dictionary<string, object?>>> GetWorkflowRouteRowsForPartAsync(NpgsqlConnection connection, int workflowPartId)
    {
        return await QueryRowsAsync(
            connection,
            """
            SELECT station_order, station_code, station_name, sample_mode, report_mode,
                   station_login_id, station_login_password, station_ip, printer_ip
            FROM workflow_routing_steps
            WHERE workflow_part_id = @workflowPartId
            ORDER BY station_order ASC, id ASC
            """,
            ("workflowPartId", workflowPartId));
    }

    private sealed record OperatorWorkflowContext(
        Dictionary<string, object?> OperatorStation,
        Dictionary<string, object?> Serial,
        Dictionary<string, object?> Selected,
        List<Dictionary<string, object?>> RouteRows,
        int CurrentOrder,
        int SelectedOrder);

    private sealed record WorkflowBomBindingStatus(
        object Payload,
        int RequiredTotal,
        int BoundTotal,
        int Remaining,
        bool RequiresBinding);

    private sealed record SamplingDecision(
        bool IsEnabled,
        bool IsRequired,
        string SamplingType,
        string Reason,
        int GeneratedIndex,
        int IntervalQty,
        int SampleQty,
        int LotSize);

    private static async Task<(OperatorWorkflowContext? Context, IResult? Error)> ResolveOperatorWorkflowContextAsync(
        NpgsqlConnection connection,
        string query,
        string loginId,
        int? requestedWorkflowPartId = null)
    {
        var operatorStation = await GetOperatorStationByLoginAsync(connection, loginId, requestedWorkflowPartId);
        if (operatorStation is null)
        {
            return (null, JsonMessage("Station login session is no longer assigned. Please login again", 401));
        }

        var serial = await GetWorkflowSerialByQueryAsync(connection, query);
        if (serial is null)
        {
            return (null, JsonMessage("SN/RSN not found", 404));
        }

        var workflowPartId = Convert.ToInt32(serial["workflow_part_id"]);
        if (Convert.ToInt32(operatorStation["workflow_part_id"]) != workflowPartId)
        {
            return (null, JsonMessage("This station login is not assigned to this serial number part number", 409));
        }

        var routeRows = await GetWorkflowRouteRowsForPartAsync(connection, workflowPartId);
        var selected = routeRows.FirstOrDefault(step =>
            string.Equals(step["station_code"]?.ToString(), operatorStation["station_code"]?.ToString(), StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            return (null, JsonMessage("Logged-in station is not in this serial route", 409));
        }

        var currentOrder = ResolveCurrentOrder(serial, routeRows);
        var selectedOrder = Convert.ToInt32(selected["station_order"]);
        var serialStatus = serial["serial_status"]?.ToString();
        if (selectedOrder < currentOrder || (selectedOrder == currentOrder && string.Equals(serialStatus, "Completed", StringComparison.OrdinalIgnoreCase)))
        {
            var repairRedirectMessage = await ResolveRepairRedirectMessageAsync(connection, serial, workflowPartId, routeRows, selected, query);
            if (repairRedirectMessage is not null)
            {
                return (null, JsonMessage(repairRedirectMessage, 409));
            }

            return (null, JsonMessage("Station is already passed", 409));
        }

        var pendingRepairStep = FindPendingRepairStep(serial, routeRows, selected);
        if (pendingRepairStep is not null)
        {
            var stationName = GetStationDisplayName(pendingRepairStep);
            return (null, JsonMessage($"Repair station \"{stationName}\" is not passed. Please pass repair station before continuing.", 409));
        }

        var blockingStep = FindBlockingRequiredStep(routeRows, currentOrder, selectedOrder);
        if (blockingStep is not null)
        {
            var stationName = GetStationDisplayName(blockingStep);
            return (null, JsonMessage($"Previous station \"{stationName}\" is not passed", 409));
        }

        var failedPreviousStep = await FindLatestFailedPreviousStepAsync(connection, serial["id"]!, workflowPartId, routeRows, selected);
        if (failedPreviousStep is not null)
        {
            var stationName = GetStationDisplayName(failedPreviousStep);
            return (null, JsonMessage($"Previous station \"{stationName}\" is failed. Please pass that station before continuing.", 409));
        }

        return (new OperatorWorkflowContext(operatorStation, serial, selected, routeRows, currentOrder, selectedOrder), null);
    }

    private static async Task<Dictionary<string, object?>?> GetOperatorStationByLoginAsync(
        NpgsqlConnection connection,
        string loginId,
        int? workflowPartId = null,
        string? stationCode = null)
    {
        await EnsureWorkflowStationLoginsTableAsync(connection);
        var rows = await QueryRowsAsync(
            connection,
            """
            SELECT
              station.station_code,
              station.station_name,
              station.station_order,
              station.workflow_part_id,
              station.station_login_id,
              station.box_qty,
              station.workflow_work_order_id
            FROM (
                SELECT
                  r.station_code,
                  r.station_name,
                  r.station_order,
                  r.workflow_part_id,
                  l.station_login_id,
                  p.box_qty,
                  COALESCE(w.id, latest_w.id) AS workflow_work_order_id,
                  l.updated_at,
                  l.id,
                  0 AS source_priority
                FROM workflow_station_logins l
                JOIN workflow_routing_steps r ON r.id = l.workflow_routing_step_id
                JOIN workflow_part_numbers p ON p.id = r.workflow_part_id
                LEFT JOIN workflow_work_orders w ON w.id = l.workflow_work_order_id
                LEFT JOIN LATERAL (
                    SELECT ww.id
                    FROM workflow_work_orders ww
                    WHERE ww.workflow_part_id = p.id
                    ORDER BY ww.updated_at DESC NULLS LAST, ww.id DESC
                    LIMIT 1
                ) latest_w ON TRUE
                WHERE UPPER(l.station_login_id) = UPPER(@loginId)
                  AND (@workflowPartId::integer IS NULL OR r.workflow_part_id = @workflowPartId::integer)
                  AND (@stationCode::text = '' OR UPPER(r.station_code) = UPPER(@stationCode::text))

                UNION ALL

                SELECT
                  r.station_code,
                  r.station_name,
                  r.station_order,
                  p.id AS workflow_part_id,
                  r.station_login_id,
                  p.box_qty,
                  w.id AS workflow_work_order_id,
                  r.updated_at,
                  r.id,
                  1 AS source_priority
                FROM item_routing_steps r
                JOIN items i ON i.id = r.item_id
                LEFT JOIN workflow_part_numbers p ON UPPER(p.pn) = UPPER(i.pn)
                LEFT JOIN LATERAL (
                    SELECT ww.id
                    FROM workflow_work_orders ww
                    WHERE ww.workflow_part_id = p.id
                    ORDER BY ww.updated_at DESC NULLS LAST, ww.id DESC
                    LIMIT 1
                ) w ON TRUE
                WHERE UPPER(r.station_login_id) = UPPER(@loginId)
                  AND (@workflowPartId::integer IS NULL OR p.id = @workflowPartId::integer)
                  AND (@stationCode::text = '' OR UPPER(r.station_code) = UPPER(@stationCode::text))

                UNION ALL

                SELECT
                  r.station_code,
                  r.station_name,
                  r.station_order,
                  r.workflow_part_id,
                  r.station_login_id,
                  p.box_qty,
                  w.id AS workflow_work_order_id,
                  r.updated_at,
                  r.id,
                  2 AS source_priority
                FROM workflow_routing_steps r
                JOIN workflow_part_numbers p ON p.id = r.workflow_part_id
                LEFT JOIN LATERAL (
                    SELECT ww.id
                    FROM workflow_work_orders ww
                    WHERE ww.workflow_part_id = p.id
                    ORDER BY ww.updated_at DESC NULLS LAST, ww.id DESC
                    LIMIT 1
                ) w ON TRUE
                WHERE UPPER(r.station_login_id) = UPPER(@loginId)
                  AND (@workflowPartId::integer IS NULL OR r.workflow_part_id = @workflowPartId::integer)
                  AND (@stationCode::text = '' OR UPPER(r.station_code) = UPPER(@stationCode::text))
            ) station
            ORDER BY station.source_priority ASC, station.updated_at DESC, station.id DESC
            LIMIT 1
            """,
            ("loginId", loginId),
            ("workflowPartId", workflowPartId),
            ("stationCode", stationCode?.Trim() ?? string.Empty));
        return rows.Count == 0 ? null : rows[0];
    }

    private static async Task<bool> HasValidOperatorCredentialsAsync(
        NpgsqlConnection connection,
        string loginId,
        string password)
    {
        var station = await GetOperatorStationByCredentialsAsync(connection, loginId, password);
        return station is not null;
    }

    private static async Task<Dictionary<string, object?>?> GetOperatorStationByCredentialsAsync(
        NpgsqlConnection connection,
        string loginId,
        string password,
        int? workflowPartId = null,
        string? stationCode = null)
    {
        await EnsureWorkflowStationLoginsTableAsync(connection);
        var rows = await QueryRowsAsync(
            connection,
            """
            SELECT
              station.station_code,
              station.station_name,
              station.station_order,
              station.workflow_part_id,
              station.station_login_id,
              station.box_qty,
              station.workflow_work_order_id
            FROM (
                SELECT
                  r.station_code,
                  r.station_name,
                  r.station_order,
                  r.workflow_part_id,
                  l.station_login_id,
                  p.box_qty,
                  COALESCE(w.id, latest_w.id) AS workflow_work_order_id,
                  l.updated_at,
                  l.id,
                  0 AS source_priority
                FROM workflow_station_logins l
                JOIN workflow_routing_steps r ON r.id = l.workflow_routing_step_id
                JOIN workflow_part_numbers p ON p.id = r.workflow_part_id
                LEFT JOIN workflow_work_orders w ON w.id = l.workflow_work_order_id
                LEFT JOIN LATERAL (
                    SELECT ww.id
                    FROM workflow_work_orders ww
                    WHERE ww.workflow_part_id = p.id
                    ORDER BY ww.updated_at DESC NULLS LAST, ww.id DESC
                    LIMIT 1
                ) latest_w ON TRUE
                WHERE UPPER(l.station_login_id) = UPPER(@loginId)
                  AND l.station_login_password = @password
                  AND (@workflowPartId::integer IS NULL OR r.workflow_part_id = @workflowPartId::integer)
                  AND (@stationCode::text = '' OR UPPER(r.station_code) = UPPER(@stationCode::text))

                UNION ALL

                SELECT
                  r.station_code,
                  r.station_name,
                  r.station_order,
                  p.id AS workflow_part_id,
                  r.station_login_id,
                  p.box_qty,
                  w.id AS workflow_work_order_id,
                  r.updated_at,
                  r.id,
                  1 AS source_priority
                FROM item_routing_steps r
                JOIN items i ON i.id = r.item_id
                LEFT JOIN workflow_part_numbers p ON UPPER(p.pn) = UPPER(i.pn)
                LEFT JOIN LATERAL (
                    SELECT ww.id
                    FROM workflow_work_orders ww
                    WHERE ww.workflow_part_id = p.id
                    ORDER BY ww.updated_at DESC NULLS LAST, ww.id DESC
                    LIMIT 1
                ) w ON TRUE
                WHERE UPPER(r.station_login_id) = UPPER(@loginId)
                  AND r.station_login_password = @password
                  AND (@workflowPartId::integer IS NULL OR p.id = @workflowPartId::integer)
                  AND (@stationCode::text = '' OR UPPER(r.station_code) = UPPER(@stationCode::text))

                UNION ALL

                SELECT
                  r.station_code,
                  r.station_name,
                  r.station_order,
                  r.workflow_part_id,
                  r.station_login_id,
                  p.box_qty,
                  w.id AS workflow_work_order_id,
                  r.updated_at,
                  r.id,
                  2 AS source_priority
                FROM workflow_routing_steps r
                JOIN workflow_part_numbers p ON p.id = r.workflow_part_id
                LEFT JOIN LATERAL (
                    SELECT ww.id
                    FROM workflow_work_orders ww
                    WHERE ww.workflow_part_id = p.id
                    ORDER BY ww.updated_at DESC NULLS LAST, ww.id DESC
                    LIMIT 1
                ) w ON TRUE
                WHERE UPPER(r.station_login_id) = UPPER(@loginId)
                  AND r.station_login_password = @password
                  AND (@workflowPartId::integer IS NULL OR r.workflow_part_id = @workflowPartId::integer)
                  AND (@stationCode::text = '' OR UPPER(r.station_code) = UPPER(@stationCode::text))
            ) station
            ORDER BY station.source_priority ASC, station.updated_at DESC, station.id DESC
            LIMIT 1
            """,
            ("loginId", loginId),
            ("password", password),
            ("workflowPartId", workflowPartId),
            ("stationCode", stationCode?.Trim() ?? string.Empty));
        return rows.Count == 0 ? null : rows[0];
    }

    private sealed record LegacyWsContext(
        string Func,
        string UserId,
        string Password,
        string Sn,
        string StationCode,
        Dictionary<string, string> SnValues,
        Dictionary<string, object?> CredentialStation,
        Dictionary<string, object?> Serial,
        Dictionary<string, object?> RequestedStation,
        List<Dictionary<string, object?>> RouteRows,
        List<Dictionary<string, object?>> History);

    private static async Task<IResult> HandleLegacyWsSnStatusAsync(HttpRequest request)
    {
        await using var connection = await OpenConnectionAsync();
        var (context, error) = await ResolveLegacyWsContextAsync(connection, request, "snstatus", validateStationProgress: false);
        if (error is not null)
        {
            return error;
        }

        var legacy = FilterLegacyContextToRequestedStation(context!);
        var latestRequestedResult = legacy.History.FirstOrDefault();
        if (latestRequestedResult is not null &&
            string.Equals(latestRequestedResult["result"]?.ToString(), "FAIL", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyWsFail(legacy.Func, "Station already failed", 409, legacy);
        }

        var currentOrder = ResolveCurrentOrder(legacy.Serial, legacy.RouteRows);
        var selectedOrder = Convert.ToInt32(legacy.RequestedStation["station_order"]);
        var blockingStep = FindBlockingRequiredStep(legacy.RouteRows, currentOrder, selectedOrder);
        if (blockingStep is not null)
        {
            var stationName = GetStationDisplayName(blockingStep);
            return LegacyWsFail(legacy.Func, $"Previous station \"{stationName}\" is not passed", 409, legacy);
        }

        var requestedStationPassed = latestRequestedResult is not null &&
            string.Equals(latestRequestedResult["result"]?.ToString(), "PASS", StringComparison.OrdinalIgnoreCase);
        if (!requestedStationPassed)
        {
            var reason = legacy.SnValues.Count > 0
                ? "Station is not passed. SN values cannot be saved."
                : "Station is not passed";
            return LegacyWsFail(legacy.Func, reason, 409, legacy);
        }

        if (legacy.SnValues.Count > 0)
        {
            await UpsertSerialExternalValuesAsync(connection, legacy.Serial, legacy.RequestedStation, legacy.UserId, legacy.SnValues);
        }

        var externalValues = await GetSerialExternalValuesAsync(connection, legacy.Serial["id"]!);
        return LegacyWsPass(legacy, string.Empty, externalValues);
    }

    private static async Task<IResult> HandleLegacyWsSnHistoryAsync(HttpRequest request)
    {
        await using var connection = await OpenConnectionAsync();
        var (context, error) = await ResolveLegacyWsContextAsync(connection, request, "snhistory", validateStationProgress: false);
        if (error is not null)
        {
            return error;
        }

        var legacy = FilterLegacyContextToRequestedStation(context!);
        var externalValues = await GetSerialExternalValuesAsync(connection, legacy.Serial["id"]!);
        return LegacyWsPass(legacy, "SN history returned", externalValues);
    }

    private static async Task<IResult> HandleLegacyWsInsertResultAsync(HttpRequest request)
    {
        await using var connection = await OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var (context, error) = await ResolveLegacyWsContextAsync(connection, request, "insertresult");
            if (error is not null)
            {
                await transaction.RollbackAsync();
                return error;
            }

            var legacy = context!;
            var requestedResult = request.Query["result"].ToString().Trim();
            var result = string.IsNullOrWhiteSpace(requestedResult) ? "PASS" : requestedResult.ToUpperInvariant();
            if (result is not "PASS" and not "FAIL")
            {
                await transaction.RollbackAsync();
                return LegacyWsFail(legacy.Func, "result must be PASS or FAIL", 400, legacy);
            }

            var remark = request.Query["remark"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(remark))
            {
                remark = result == "PASS" ? "Legacy MES insertresult PASS" : "Legacy MES insertresult FAIL";
            }

            if (legacy.SnValues.Count > 0)
            {
                await UpsertSerialExternalValuesAsync(connection, legacy.Serial, legacy.RequestedStation, legacy.UserId, legacy.SnValues);
            }

            if (result == "FAIL")
            {
                await ExecuteAsync(
                    connection,
                    """
                    UPDATE workflow_serial_numbers
                    SET status = 'Failed',
                        condition = 'Bad',
                        current_station_code = @stationCode,
                        current_station_order = @stationOrder,
                        last_moved_at = NOW(),
                        updated_at = NOW()
                    WHERE id = @id
                    """,
                    ("stationCode", legacy.RequestedStation["station_code"]),
                    ("stationOrder", legacy.RequestedStation["station_order"]),
                    ("id", legacy.Serial["id"]));

                await InsertWorkflowStationLogAsync(
                    connection,
                    legacy.Serial,
                    legacy.RequestedStation,
                    "FAIL",
                    remark,
                    legacy.UserId,
                    legacy.Serial["current_station_code"],
                    legacy.Serial["current_station_order"],
                    legacy.RequestedStation["station_code"],
                    legacy.RequestedStation["station_order"]);

                await transaction.CommitAsync();
                var failedValues = await GetSerialExternalValuesAsync(connection, legacy.Serial["id"]!);
                return LegacyWsFail(legacy.Func, remark, 200, legacy, failedValues);
            }

            var selectedOrder = Convert.ToInt32(legacy.RequestedStation["station_order"]);
            var nextStep = legacy.RouteRows.FirstOrDefault(step => Convert.ToInt32(step["station_order"]) > selectedOrder);
            var nextStatus = nextStep is null ? "Completed" : "In Process";
            var nextStationCode = nextStep?["station_code"] ?? legacy.RequestedStation["station_code"];
            var nextStationOrder = nextStep?["station_order"] ?? legacy.RequestedStation["station_order"];

            await ExecuteAsync(
                connection,
                """
                UPDATE workflow_serial_numbers
                SET status = @status,
                    condition = 'Good',
                    current_station_code = @stationCode,
                    current_station_order = @stationOrder,
                    last_moved_at = NOW(),
                    updated_at = NOW()
                WHERE id = @id
                """,
                ("status", nextStatus),
                ("stationCode", nextStationCode),
                ("stationOrder", nextStationOrder),
                ("id", legacy.Serial["id"]));

            await InsertWorkflowStationLogAsync(
                connection,
                legacy.Serial,
                legacy.RequestedStation,
                "PASS",
                remark,
                legacy.UserId,
                legacy.Serial["current_station_code"],
                legacy.Serial["current_station_order"],
                nextStationCode,
                nextStationOrder);

            await MirrorWorkflowPassToLegacyTraceAsync(
                connection,
                legacy.Serial,
                legacy.RequestedStation,
                "PASS",
                remark,
                legacy.UserId,
                legacy.Serial["current_station_code"],
                legacy.Serial["current_station_order"],
                nextStationCode,
                nextStationOrder,
                nextStatus);

            await transaction.CommitAsync();
            var externalValues = await GetSerialExternalValuesAsync(connection, legacy.Serial["id"]!);
            return LegacyWsPass(legacy, "Station result inserted", externalValues);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static async Task<(LegacyWsContext? Context, IResult? Error)> ResolveLegacyWsContextAsync(
        NpgsqlConnection connection,
        HttpRequest request,
        string func,
        bool validateStationProgress = true)
    {
        var userId = request.Query["user_id"].ToString().Trim();
        var password = request.Query["password"].ToString();
        var sn = request.Query["sn"].ToString().Trim();
        var stationCode = request.Query["station_code"].ToString().Trim();
        var snValues = ParseLegacySnValues(request.Query["sn_values"].ToString());

        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(password))
        {
            return (null, LegacyWsSimpleFail(func, "user_id and password are required", 400));
        }

        if (string.IsNullOrWhiteSpace(sn))
        {
            return (null, LegacyWsSimpleFail(func, "sn is required", 400));
        }

        if (string.IsNullOrWhiteSpace(stationCode))
        {
            return (null, LegacyWsSimpleFail(func, "station_code is required", 400));
        }

        await EnsureWorkflowSchemaAsync(connection);
        await EnsureWorkflowStationLoginsTableAsync(connection);
        await EnsureSerialExternalValuesTableAsync(connection);

        var serial = await GetWorkflowSerialByQueryAsync(connection, sn);
        if (serial is null)
        {
            return (null, LegacyWsSimpleFail(func, "Serial Number not found", 404));
        }

        if (serial["workflow_work_order_id"] is null || string.IsNullOrWhiteSpace(serial["wo"]?.ToString()))
        {
            return (null, LegacyWsSimpleFail(func, "Work Order not found", 404, serial: serial));
        }

        var workflowPartId = Convert.ToInt32(serial["workflow_part_id"]);
        var routeRows = await GetWorkflowRouteRowsForPartAsync(connection, workflowPartId);
        if (routeRows.Count == 0)
        {
            return (null, LegacyWsSimpleFail(func, "Routing not found", 404, serial: serial));
        }

        var currentOrder = ResolveCurrentOrder(serial, routeRows);
        var requestedStation = ResolveExternalRequestedStation(serial, routeRows, stationCode, currentOrder);
        if (requestedStation is null)
        {
            return (null, LegacyWsSimpleFail(func, "Station routing not found", 404, serial: serial));
        }

        var credentialStation = await GetOperatorStationByCredentialsAsync(
            connection,
            userId,
            password,
            workflowPartId,
            requestedStation["station_code"]?.ToString());
        if (credentialStation is null)
        {
            return (null, LegacyWsSimpleFail(func, "Invalid operator credentials", 401, serial: serial, currentStation: requestedStation));
        }

        var history = await BuildExternalSnHistoryAsync(connection, serial, routeRows);
        var requestedStationCode = requestedStation["station_code"]?.ToString() ?? string.Empty;
        var stationHistory = history
            .Where(row => string.Equals(row["stationCode"]?.ToString(), requestedStationCode, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var latestRequestedResult = stationHistory.FirstOrDefault(row =>
            string.Equals(row["stationCode"]?.ToString(), requestedStationCode, StringComparison.OrdinalIgnoreCase));

        if (latestRequestedResult is not null &&
            string.Equals(latestRequestedResult["result"]?.ToString(), "FAIL", StringComparison.OrdinalIgnoreCase))
        {
            return (null, LegacyWsSimpleFail(func, "Station already failed", 409, serial, requestedStation, stationHistory));
        }

        if (validateStationProgress)
        {
            var selectedOrder = Convert.ToInt32(requestedStation["station_order"]);
            var serialStatus = serial["serial_status"]?.ToString();
            if (selectedOrder < currentOrder ||
                (selectedOrder == currentOrder && string.Equals(serialStatus, "Completed", StringComparison.OrdinalIgnoreCase)))
            {
                return (null, LegacyWsSimpleFail(func, "Station is already passed", 409, serial, requestedStation, history));
            }

            var pendingRepairStep = FindPendingRepairStep(serial, routeRows, requestedStation);
            if (pendingRepairStep is not null)
            {
                var stationName = GetStationDisplayName(pendingRepairStep);
                return (null, LegacyWsSimpleFail(func, $"Repair station \"{stationName}\" is not passed. Please pass repair station before continuing.", 409, serial, requestedStation, history));
            }

            var blockingStep = FindBlockingRequiredStep(routeRows, currentOrder, selectedOrder);
            if (blockingStep is not null)
            {
                var stationName = GetStationDisplayName(blockingStep);
                return (null, LegacyWsSimpleFail(func, $"Previous station \"{stationName}\" is not passed", 409, serial, requestedStation, history));
            }
        }

        return (new LegacyWsContext(func, userId, password, sn, stationCode, snValues, credentialStation, serial, requestedStation, routeRows, history), null);
    }

    private static LegacyWsContext FilterLegacyContextToRequestedStation(LegacyWsContext context)
    {
        var stationCode = context.RequestedStation["station_code"]?.ToString() ?? string.Empty;
        var stationHistory = context.History
            .Where(row => string.Equals(row["stationCode"]?.ToString(), stationCode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return context with { History = stationHistory };
    }

    private static Dictionary<string, string> ParseLegacySnValues(string raw)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return values;
        }

        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('|');
            if (separator <= 0)
            {
                continue;
            }

            var key = pair[..separator].Trim();
            var value = pair[(separator + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                values[key] = value;
            }
        }

        return values;
    }

    private static IResult LegacyWsPass(
        LegacyWsContext context,
        string reason,
        List<Dictionary<string, object?>>? externalValues = null)
    {
        return LegacyWsResponse(true, "PASS", reason, context.Func, context.Serial, context.RequestedStation, context.History, context.SnValues, externalValues, 200);
    }

    private static IResult LegacyWsFail(
        string func,
        string reason,
        int statusCode,
        LegacyWsContext? context = null,
        List<Dictionary<string, object?>>? externalValues = null)
    {
        return LegacyWsResponse(false, "FAIL", reason, func, context?.Serial, context?.RequestedStation, context?.History, context?.SnValues, externalValues, statusCode);
    }

    private static IResult LegacyWsSimpleFail(
        string func,
        string reason,
        int statusCode,
        Dictionary<string, object?>? serial = null,
        Dictionary<string, object?>? currentStation = null,
        List<Dictionary<string, object?>>? history = null)
    {
        return LegacyWsResponse(false, "FAIL", reason, func, serial, currentStation, history, null, null, statusCode);
    }

    private static IResult LegacyWsResponse(
        bool success,
        string result,
        string reason,
        string func,
        Dictionary<string, object?>? serial,
        Dictionary<string, object?>? currentStation,
        List<Dictionary<string, object?>>? history,
        Dictionary<string, string>? snValues,
        List<Dictionary<string, object?>>? externalValues,
        int statusCode)
    {
        return Results.Json(new
        {
            success,
            result,
            reason,
            func,
            sn = serial?["sn"]?.ToString() ?? string.Empty,
            rsn = serial?["rsn"]?.ToString() ?? string.Empty,
            wo = serial?["wo"]?.ToString() ?? string.Empty,
            pn = serial?["pn"]?.ToString() ?? string.Empty,
            station_code = currentStation?["station_code"]?.ToString() ?? serial?["current_station_code"]?.ToString() ?? string.Empty,
            station_name = currentStation?["station_name"]?.ToString() ?? string.Empty,
            sn_values = snValues ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        }, statusCode: statusCode);
    }

    private static Dictionary<string, object?>? ResolveExternalRequestedStation(
        Dictionary<string, object?> serial,
        List<Dictionary<string, object?>> routeRows,
        string stationCode,
        int currentOrder)
    {
        if (!string.IsNullOrWhiteSpace(stationCode))
        {
            return routeRows.FirstOrDefault(step =>
                string.Equals(step["station_code"]?.ToString(), stationCode, StringComparison.OrdinalIgnoreCase));
        }

        var currentStation = routeRows.FirstOrDefault(step => Convert.ToInt32(step["station_order"]) == currentOrder);
        if (currentStation is not null)
        {
            return currentStation;
        }

        var serialStationCode = serial["current_station_code"]?.ToString();
        return routeRows.FirstOrDefault(step =>
            string.Equals(step["station_code"]?.ToString(), serialStationCode, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<List<Dictionary<string, object?>>> BuildExternalSnHistoryAsync(
        NpgsqlConnection connection,
        Dictionary<string, object?> serial,
        List<Dictionary<string, object?>> routeRows)
    {
        var logRows = await QueryRowsAsync(
            connection,
            """
            SELECT station_code, station_name, action_result, remark, created_at
            FROM workflow_serial_station_logs
            WHERE workflow_serial_id = @serialId
            ORDER BY created_at DESC, id DESC
            """,
            ("serialId", serial["id"]));

        return routeRows.Select(step =>
        {
            var stationCode = step["station_code"]?.ToString() ?? string.Empty;
            var latestLog = logRows.FirstOrDefault(log =>
                string.Equals(log["station_code"]?.ToString(), stationCode, StringComparison.OrdinalIgnoreCase));
            var result = latestLog is null ? "PENDING" : NormalizeExternalStationResult(latestLog["action_result"]?.ToString());

            return new Dictionary<string, object?>
            {
                ["stationCode"] = stationCode,
                ["stationName"] = step["station_name"]?.ToString() ?? string.Empty,
                ["result"] = result,
                ["dateTime"] = latestLog?["created_at"],
                ["reason"] = latestLog?["remark"]?.ToString() ?? string.Empty
            };
        }).ToList();
    }

    private static string NormalizeExternalStationResult(string? result)
    {
        if (string.Equals(result, "PASS", StringComparison.OrdinalIgnoreCase))
        {
            return "PASS";
        }

        if (string.Equals(result, "CANCELLED", StringComparison.OrdinalIgnoreCase))
        {
            return "CANCELLED";
        }

        if (string.Equals(result, "FAIL", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result, "NOT_PASS", StringComparison.OrdinalIgnoreCase))
        {
            return "FAIL";
        }

        return "PENDING";
    }

    private static IResult ExternalSnStatusError(
        string reason,
        int statusCode,
        Dictionary<string, object?>? serial = null)
    {
        return ExternalSnStatusResponse(false, "FAIL", reason, serial, null, new List<Dictionary<string, object?>>(), null, statusCode);
    }

    private static IResult ExternalSnStatusResponse(
        bool success,
        string result,
        string reason,
        Dictionary<string, object?>? serial,
        Dictionary<string, object?>? currentStation,
        List<Dictionary<string, object?>> history,
        List<Dictionary<string, object?>>? externalValues,
        int statusCode = 200)
    {
        return Results.Json(new
        {
            success,
            result,
            reason,
            serialNumber = serial?["sn"]?.ToString() ?? string.Empty,
            rsn = serial?["rsn"]?.ToString() ?? string.Empty,
            workOrder = serial?["wo"]?.ToString() ?? string.Empty,
            partNumber = serial?["pn"]?.ToString() ?? string.Empty,
            currentStation = currentStation?["station_code"]?.ToString() ?? serial?["current_station_code"]?.ToString() ?? string.Empty,
            values = externalValues ?? new List<Dictionary<string, object?>>(),
            history
        }, statusCode: statusCode);
    }

    private static async Task EnsureSerialExternalValuesTableAsync(NpgsqlConnection connection)
    {
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS public.serial_external_values (
              id BIGSERIAL PRIMARY KEY,
              workflow_serial_id BIGINT NOT NULL REFERENCES workflow_serial_numbers(id) ON DELETE CASCADE,
              station_code VARCHAR(80) NOT NULL,
              station_name VARCHAR(220),
              chip_id VARCHAR(220),
              imes VARCHAR(220),
              ext_values_json JSONB NOT NULL DEFAULT '{}'::jsonb,
              ext_values_text TEXT,
              pushed_by VARCHAR(160) NOT NULL,
              pushed_at TIMESTAMP NOT NULL DEFAULT NOW(),
              updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
              CONSTRAINT uq_serial_external_values_station UNIQUE (workflow_serial_id, station_code)
            )
            """);

        await ExecuteAsync(connection, "ALTER TABLE public.serial_external_values ADD COLUMN IF NOT EXISTS ext_values_json JSONB NOT NULL DEFAULT '{}'::jsonb");
        await ExecuteAsync(connection, "ALTER TABLE public.serial_external_values ADD COLUMN IF NOT EXISTS ext_values_text TEXT");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_serial_external_values_serial ON public.serial_external_values (workflow_serial_id)");
    }

    private static async Task UpsertSerialExternalValuesAsync(
        NpgsqlConnection connection,
        Dictionary<string, object?> serial,
        Dictionary<string, object?> station,
        string pushedBy,
        string chipId,
        string imes)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(chipId))
        {
            values["ChipId"] = chipId;
        }

        if (!string.IsNullOrWhiteSpace(imes))
        {
            values["IMEI"] = imes;
        }

        await UpsertSerialExternalValuesAsync(connection, serial, station, pushedBy, values);
    }

    private static async Task UpsertSerialExternalValuesAsync(
        NpgsqlConnection connection,
        Dictionary<string, object?> serial,
        Dictionary<string, object?> station,
        string pushedBy,
        Dictionary<string, string> extValues)
    {
        var chipId = ReadExternalValue(extValues, "ChipId", "CHIP_ID", "Chip ID");
        var imes = ReadExternalValue(extValues, "IMEI", "IMES", "IMEIs");
        var extValuesJson = JsonSerializer.Serialize(extValues);
        var extValuesText = string.Join(",", extValues.Select(pair => $"{pair.Key}|{pair.Value}"));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO public.serial_external_values
              (workflow_serial_id, station_code, station_name, chip_id, imes, ext_values_json, ext_values_text, pushed_by, pushed_at, updated_at)
            VALUES
              (@serialId, @stationCode, @stationName, @chipId, @imes, CAST(@extValuesJson AS jsonb), @extValuesText, @pushedBy, NOW(), NOW())
            ON CONFLICT (workflow_serial_id, station_code)
            DO UPDATE SET
              station_name = EXCLUDED.station_name,
              chip_id = COALESCE(NULLIF(EXCLUDED.chip_id, ''), public.serial_external_values.chip_id),
              imes = COALESCE(NULLIF(EXCLUDED.imes, ''), public.serial_external_values.imes),
              ext_values_json = public.serial_external_values.ext_values_json || EXCLUDED.ext_values_json,
              ext_values_text = EXCLUDED.ext_values_text,
              pushed_by = EXCLUDED.pushed_by,
              updated_at = NOW()
            """,
            ("serialId", serial["id"]),
            ("stationCode", station["station_code"]),
            ("stationName", station["station_name"]),
            ("chipId", chipId),
            ("imes", imes),
            ("extValuesJson", extValuesJson),
            ("extValuesText", extValuesText),
            ("pushedBy", pushedBy));
    }

    private static string ReadExternalValue(Dictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static async Task<List<Dictionary<string, object?>>> GetSerialExternalValuesAsync(
        NpgsqlConnection connection,
        object workflowSerialId)
    {
        return await QueryRowsAsync(
            connection,
            """
            SELECT station_code, station_name, chip_id, imes, ext_values_json, ext_values_text, pushed_by, pushed_at, updated_at
            FROM public.serial_external_values
            WHERE workflow_serial_id = @serialId
            ORDER BY updated_at DESC, id DESC
            """,
            ("serialId", workflowSerialId));
    }

    private static async Task<List<Dictionary<string, object?>>> GetWorkflowBomLinesForStationAsync(
        NpgsqlConnection connection,
        int workflowPartId,
        string stationCode)
    {
        return await QueryRowsAsync(
            connection,
            """
            SELECT b.id, b.son_pn, b.son_description, COALESCE(b.station_code, '') AS station_code,
                   COALESCE(b.station_name, '') AS station_name, COALESCE(b.item_type, '') AS item_type,
                   COALESCE(pt.code, pt.type, '') AS pn_type, b.qty
            FROM workflow_bom_children b
            LEFT JOIN workflow_part_numbers child_part ON UPPER(BTRIM(child_part.pn)) = UPPER(BTRIM(b.son_pn))
            LEFT JOIN pn_types pt ON pt.id = child_part.pn_type_id
            WHERE b.workflow_part_id = @workflowPartId
              AND UPPER(COALESCE(b.station_code, '')) = UPPER(@stationCode)
            ORDER BY b.id ASC
            """,
            ("workflowPartId", workflowPartId),
            ("stationCode", stationCode));
    }

    private static async Task<List<Dictionary<string, object?>>> GetWorkflowBomBindingsForParentStationAsync(
        NpgsqlConnection connection,
        object parentSerialId,
        string stationCode)
    {
        return await QueryRowsAsync(
            connection,
            """
            SELECT l.id, l.workflow_bom_child_id, l.station_code, l.station_name, l.created_by, l.created_at,
                   child.sn AS child_sn, child.rsn AS child_rsn, child.status AS child_status,
                   child_part.pn AS child_pn, child_part.description AS child_description
            FROM workflow_serial_bom_bindings l
            JOIN workflow_serial_numbers child ON child.id = l.child_workflow_serial_id
            JOIN workflow_part_numbers child_part ON child_part.id = child.workflow_part_id
            WHERE l.parent_workflow_serial_id = @parentId
              AND UPPER(l.station_code) = UPPER(@stationCode)
            ORDER BY l.created_at ASC, l.id ASC
            """,
            ("parentId", parentSerialId),
            ("stationCode", stationCode));
    }

    private static async Task<WorkflowBomBindingStatus> BuildWorkflowBomBindingStatusAsync(
        NpgsqlConnection connection,
        Dictionary<string, object?> serial,
        Dictionary<string, object?> station)
    {
        var stationCode = station["station_code"]?.ToString() ?? string.Empty;
        var requiredRows = await GetWorkflowBomLinesForStationAsync(connection, Convert.ToInt32(serial["workflow_part_id"]), stationCode);
        var bindings = await GetWorkflowBomBindingsForParentStationAsync(connection, serial["id"]!, stationCode);
        var boundByLine = bindings
            .GroupBy(row => Convert.ToInt32(row["workflow_bom_child_id"]))
            .ToDictionary(group => group.Key, group => group.Count());

        var required = requiredRows.Select(line =>
        {
            var lineId = Convert.ToInt32(line["id"]);
            var qty = Convert.ToInt32(line["qty"]);
            var boundQty = boundByLine.TryGetValue(lineId, out var count) ? count : 0;

            return new
            {
                id = lineId,
                son_pn = line["son_pn"],
                son_description = line["son_description"],
                station_code = line["station_code"],
                station_name = line["station_name"],
                item_type = line["item_type"],
                pn_type = line["pn_type"],
                qty,
                bound_qty = boundQty,
                remaining_qty = Math.Max(qty - boundQty, 0)
            };
        }).ToList();

        var requiredTotal = required.Sum(row => row.qty);
        var boundTotal = Math.Min(bindings.Count, requiredTotal);
        var remaining = Math.Max(requiredTotal - boundTotal, 0);
        var payload = new
        {
            parent = new
            {
                id = serial["id"],
                sn = serial["sn"],
                rsn = serial["rsn"],
                pn = serial["pn"],
                station_code = station["station_code"],
                station_name = station["station_name"]
            },
            required,
            bindings = bindings.Select(row => new
            {
                id = row["id"],
                workflow_bom_child_id = row["workflow_bom_child_id"],
                child_sn = row["child_sn"],
                child_rsn = row["child_rsn"],
                child_pn = row["child_pn"],
                child_description = row["child_description"],
                child_status = row["child_status"],
                created_at = row["created_at"]
            }).ToList(),
            requiredTotal,
            boundTotal,
            remaining,
            requiresBinding = requiredTotal > 0
        };

        return new WorkflowBomBindingStatus(payload, requiredTotal, boundTotal, remaining, requiredTotal > 0);
    }

    private static int ResolveCurrentOrder(Dictionary<string, object?> serial, List<Dictionary<string, object?>> routeRows)
    {
        if (serial["current_station_order"] is not null)
        {
            return Convert.ToInt32(serial["current_station_order"]);
        }

        var currentCode = serial["current_station_code"]?.ToString();
        var matched = routeRows.FirstOrDefault(row => string.Equals(row["station_code"]?.ToString(), currentCode, StringComparison.Ordinal));
        return matched is not null ? Convert.ToInt32(matched["station_order"]) : Convert.ToInt32(routeRows[0]["station_order"]);
    }

    private static Dictionary<string, object?>? FindBlockingRequiredStep(List<Dictionary<string, object?>> routeRows, int currentOrder, int selectedOrder)
    {
        if (selectedOrder <= currentOrder)
        {
            return null;
        }

        return routeRows.FirstOrDefault(step =>
        {
            var order = Convert.ToInt32(step["station_order"]);
            return order >= currentOrder
                && order < selectedOrder
                && !IsOptionalSampleStep(step);
        });
    }

    private static Dictionary<string, object?>? FindPendingRepairStep(
        Dictionary<string, object?> serial,
        List<Dictionary<string, object?>> routeRows,
        Dictionary<string, object?> selected)
    {
        if (!string.Equals(serial["serial_status"]?.ToString(), "Repair", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var currentStationCode = serial["current_station_code"]?.ToString();
        if (string.IsNullOrWhiteSpace(currentStationCode) ||
            string.Equals(currentStationCode, selected["station_code"]?.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var currentOrder = ResolveCurrentOrder(serial, routeRows);
        var selectedOrder = Convert.ToInt32(selected["station_order"]);
        if (selectedOrder <= currentOrder)
        {
            return null;
        }

        return routeRows.FirstOrDefault(step =>
            string.Equals(step["station_code"]?.ToString(), currentStationCode, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsOptionalSampleStep(Dictionary<string, object?> step)
    {
        return string.Equals(step["sample_mode"]?.ToString(), "Sample", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetStationDisplayName(Dictionary<string, object?> step)
    {
        return step["station_name"]?.ToString()
            ?? step["station_code"]?.ToString()
            ?? "previous station";
    }

    private static bool IsPackStation(string? stationCode, string? stationName)
    {
        var text = $"{stationCode} {stationName}";
        return text.Contains("PACK", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Dictionary<string, object?>?> FindLatestFailedPreviousStepAsync(
        NpgsqlConnection connection,
        object workflowSerialId,
        int workflowPartId,
        List<Dictionary<string, object?>> routeRows,
        Dictionary<string, object?> selected)
    {
        var selectedOrder = Convert.ToInt32(selected["station_order"]);
        var previousSteps = routeRows
            .Where(step => Convert.ToInt32(step["station_order"]) < selectedOrder)
            .Where(step => !string.IsNullOrWhiteSpace(step["station_code"]?.ToString()))
            .ToList();

        if (previousSteps.Count == 0)
        {
            return null;
        }

        var previousCodes = previousSteps
            .Select(step => step["station_code"]!.ToString()!)
            .ToArray();

        var rows = await QueryRowsAsync(
            connection,
            """
            SELECT DISTINCT ON (UPPER(station_code)) station_code, action_result, created_at
            FROM workflow_serial_station_logs
            WHERE workflow_serial_id = @serialId
              AND station_code = ANY(@stationCodes)
              AND UPPER(action_result) IN ('PASS', 'FAIL')
            ORDER BY UPPER(station_code), created_at DESC, id DESC
            """,
            ("serialId", workflowSerialId),
            ("stationCodes", previousCodes));

        var latestByStation = rows
            .Where(row => !string.IsNullOrWhiteSpace(row["station_code"]?.ToString()))
            .ToDictionary(row => row["station_code"]!.ToString()!, row => row, StringComparer.OrdinalIgnoreCase);

        foreach (var step in previousSteps)
        {
            var stationCode = step["station_code"]?.ToString() ?? string.Empty;
            if (!latestByStation.TryGetValue(stationCode, out var latest) ||
                !string.Equals(latest["action_result"]?.ToString(), "FAIL", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (await IsSelectedRepairStationForFailedStepAsync(connection, workflowPartId, routeRows, step, selected))
            {
                continue;
            }

            return step;
        }

        return null;
    }

    private static async Task<Dictionary<string, object?>?> FindRepairReturnStepAsync(
        NpgsqlConnection connection,
        object workflowSerialId,
        int workflowPartId,
        List<Dictionary<string, object?>> routeRows,
        Dictionary<string, object?> selected)
    {
        var selectedOrder = Convert.ToInt32(selected["station_order"]);
        var previousSteps = routeRows
            .Where(step => Convert.ToInt32(step["station_order"]) < selectedOrder)
            .Where(step => !string.IsNullOrWhiteSpace(step["station_code"]?.ToString()))
            .ToList();

        if (previousSteps.Count == 0)
        {
            return null;
        }

        var previousCodes = previousSteps
            .Select(step => step["station_code"]!.ToString()!)
            .ToArray();

        var rows = await QueryRowsAsync(
            connection,
            """
            SELECT DISTINCT ON (UPPER(station_code)) station_code, action_result
            FROM workflow_serial_station_logs
            WHERE workflow_serial_id = @serialId
              AND station_code = ANY(@stationCodes)
              AND UPPER(action_result) IN ('PASS', 'FAIL')
            ORDER BY UPPER(station_code), created_at DESC, id DESC
            """,
            ("serialId", workflowSerialId),
            ("stationCodes", previousCodes));

        var latestByStation = rows
            .Where(row => !string.IsNullOrWhiteSpace(row["station_code"]?.ToString()))
            .ToDictionary(row => row["station_code"]!.ToString()!, row => row, StringComparer.OrdinalIgnoreCase);

        foreach (var step in previousSteps)
        {
            var stationCode = step["station_code"]?.ToString() ?? string.Empty;
            if (!latestByStation.TryGetValue(stationCode, out var latest) ||
                !string.Equals(latest["action_result"]?.ToString(), "FAIL", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (await IsSelectedRepairStationForFailedStepAsync(connection, workflowPartId, routeRows, step, selected))
            {
                return step;
            }
        }

        return null;
    }

    private static async Task<bool> IsSelectedRepairStationForFailedStepAsync(
        NpgsqlConnection connection,
        int workflowPartId,
        List<Dictionary<string, object?>> routeRows,
        Dictionary<string, object?> failedStep,
        Dictionary<string, object?> selected)
    {
        var repairConfig = await GetWorkflowRepairStationConfigAsync(connection, workflowPartId, failedStep["station_code"]?.ToString());
        if (repairConfig is null ||
            repairConfig["is_repair_station_enabled"] is not bool enabled ||
            !enabled)
        {
            return false;
        }

        var repairStep = ResolveRepairRouteStep(routeRows, repairConfig["repair_station_name"]?.ToString());
        if (repairStep is null)
        {
            return false;
        }

        return string.Equals(repairStep["station_code"]?.ToString(), selected["station_code"]?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> ResolveRepairRedirectMessageAsync(
        NpgsqlConnection connection,
        Dictionary<string, object?> serial,
        int workflowPartId,
        List<Dictionary<string, object?>> routeRows,
        Dictionary<string, object?> selected,
        string query)
    {
        if (!string.Equals(serial["serial_status"]?.ToString(), "Repair", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var repairConfig = await GetWorkflowRepairStationConfigAsync(connection, workflowPartId, selected["station_code"]?.ToString());
        if (repairConfig is null ||
            repairConfig["is_repair_station_enabled"] is not bool enabled ||
            !enabled)
        {
            return null;
        }

        var repairStep = ResolveRepairRouteStep(routeRows, repairConfig["repair_station_name"]?.ToString());
        if (repairStep is null ||
            !string.Equals(repairStep["station_code"]?.ToString(), serial["current_station_code"]?.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var serialNumber = serial["sn"]?.ToString() ?? query;
        return $"{serialNumber} went to repair station {GetStationDisplayName(repairStep)}.";
    }

    private static async Task<int> GetContinuousWorkflowFailureCountAsync(
        NpgsqlConnection connection,
        object workflowSerialId,
        string? stationCode)
    {
        if (string.IsNullOrWhiteSpace(stationCode))
        {
            return 0;
        }

        var rows = await QueryRowsAsync(
            connection,
            """
            WITH last_pass AS (
                SELECT COALESCE(MAX(created_at), '-infinity'::timestamp) AS passed_at
                FROM workflow_serial_station_logs
                WHERE workflow_serial_id = @serialId
                  AND UPPER(station_code) = UPPER(@stationCode)
                  AND UPPER(action_result) = 'PASS'
            )
            SELECT COUNT(*)::int AS fail_count
            FROM workflow_serial_station_logs logs, last_pass
            WHERE logs.workflow_serial_id = @serialId
              AND UPPER(logs.station_code) = UPPER(@stationCode)
              AND UPPER(logs.action_result) = 'FAIL'
              AND logs.created_at > last_pass.passed_at
            """,
            ("serialId", workflowSerialId),
            ("stationCode", stationCode));

        return rows.Count > 0 ? Convert.ToInt32(rows[0]["fail_count"] ?? 0) : 0;
    }

    private static async Task<Dictionary<string, object?>?> GetWorkflowRepairStationConfigAsync(
        NpgsqlConnection connection,
        int workflowPartId,
        string? stationCode)
    {
        if (string.IsNullOrWhiteSpace(stationCode))
        {
            return null;
        }

        var rows = await QueryRowsAsync(
            connection,
            """
            SELECT repair_station_name, is_repair_station_enabled
            FROM workflow_station_repair
            WHERE workflow_part_id = @workflowPartId
              AND UPPER(station_code) = UPPER(@stationCode)
            LIMIT 1
            """,
            ("workflowPartId", workflowPartId),
            ("stationCode", stationCode));

        return rows.Count == 0 ? null : rows[0];
    }

    private static Dictionary<string, object?>? ResolveRepairRouteStep(
        List<Dictionary<string, object?>> routeRows,
        string? repairStationName)
    {
        var repairName = repairStationName?.Trim();
        if (string.IsNullOrWhiteSpace(repairName))
        {
            return null;
        }

        return routeRows.FirstOrDefault(step =>
            string.Equals(step["station_code"]?.ToString(), repairName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(step["station_name"]?.ToString(), repairName, StringComparison.OrdinalIgnoreCase));
    }

    private static string ToOrdinal(int value)
    {
        return value switch
        {
            1 => "first",
            2 => "second",
            3 => "third",
            _ => $"{value}th"
        };
    }

    private static async Task<SamplingDecision> ResolveSamplingDecisionAsync(
        NpgsqlConnection connection,
        int workflowPartId,
        Dictionary<string, object?> station,
        Dictionary<string, object?> serial)
    {
        if (!string.Equals(station["sample_mode"]?.ToString(), "Sample", StringComparison.OrdinalIgnoreCase))
        {
            return new SamplingDecision(false, true, "FULL", "Full station", 0, 0, 0, 0);
        }

        var rows = await QueryRowsAsync(
            connection,
            """
            SELECT sampling_type, interval_qty, sample_qty, lot_size, is_sampling_enabled
            FROM workflow_station_sampling
            WHERE workflow_part_id = @workflowPartId
              AND UPPER(station_code) = UPPER(@stationCode)
            LIMIT 1
            """,
            ("workflowPartId", workflowPartId),
            ("stationCode", station["station_code"]));

        if (rows.Count == 0 || rows[0]["is_sampling_enabled"] is not bool enabled || !enabled)
        {
            return new SamplingDecision(false, true, "DISABLED", "Sampling disabled", 0, 0, 0, 0);
        }

        var generatedIndex = Math.Max(1, Convert.ToInt32(serial["generated_index"] ?? 1));
        var samplingType = (rows[0]["sampling_type"]?.ToString() ?? "PERIODIC").Trim().ToUpperInvariant();
        var intervalQty = Math.Max(1, Convert.ToInt32(rows[0]["interval_qty"] ?? 10));
        var sampleQty = Math.Max(1, Convert.ToInt32(rows[0]["sample_qty"] ?? 1));
        var lotSize = Math.Max(1, Convert.ToInt32(rows[0]["lot_size"] ?? 1000));
        var isRequired = samplingType switch
        {
            "FIRST_PIECE" => generatedIndex == 1,
            "LOT" => ((generatedIndex - 1) % lotSize) < Math.Min(sampleQty, lotSize),
            "RANDOM" => IsRandomSampleSelected(
                generatedIndex,
                intervalQty,
                sampleQty,
                $"{workflowPartId}:{station["station_code"]}"),
            _ => ((generatedIndex - 1) % intervalQty) < Math.Min(sampleQty, intervalQty)
        };
        var reason = isRequired
            ? $"{samplingType} selected SN index {generatedIndex}"
            : $"{samplingType} did not select SN index {generatedIndex}";

        return new SamplingDecision(true, isRequired, samplingType, reason, generatedIndex, intervalQty, sampleQty, lotSize);
    }

    private static bool IsRandomSampleSelected(int generatedIndex, int intervalQty, int sampleQty, string seedPrefix)
    {
        intervalQty = Math.Max(1, intervalQty);
        sampleQty = Math.Clamp(sampleQty, 1, intervalQty);

        var zeroBasedIndex = Math.Max(0, generatedIndex - 1);
        var groupIndex = zeroBasedIndex / intervalQty;
        var positionInGroup = zeroBasedIndex % intervalQty;
        var selectedPositions = Enumerable
            .Range(0, intervalQty)
            .OrderBy(position => StableSamplingHash($"{seedPrefix}:{groupIndex}:{position}"))
            .Take(sampleQty)
            .ToHashSet();

        return selectedPositions.Contains(positionInGroup);
    }

    private static ulong StableSamplingHash(string value)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;

        foreach (var character in value)
        {
            hash ^= character;
            hash *= prime;
        }

        return hash;
    }

    private static async Task InsertWorkflowStationLogAsync(
        NpgsqlConnection connection,
        Dictionary<string, object?> serial,
        Dictionary<string, object?> station,
        string result,
        string remark,
        string changedBy,
        object? beforeStationCode,
        object? beforeStationOrder,
        object? afterStationCode,
        object? afterStationOrder,
        string? debugRemark = null)
    {
        await ExecuteAsync(
            connection,
            """
            INSERT INTO workflow_serial_station_logs
              (workflow_serial_id, workflow_part_id, workflow_work_order_id, station_code, station_name,
               action_result, remark, changed_by, before_station_code, before_station_order,
               after_station_code, after_station_order, debug_remark)
            VALUES
              (@serialId, @partId, @workOrderId, @stationCode, @stationName,
               @result, @remark, @changedBy, @beforeCode, @beforeOrder, @afterCode, @afterOrder, @debugRemark)
            """,
            ("serialId", serial["id"]),
            ("partId", serial["workflow_part_id"]),
            ("workOrderId", serial["workflow_work_order_id"]),
            ("stationCode", station["station_code"]),
            ("stationName", station["station_name"]),
            ("result", result),
            ("remark", remark),
            ("changedBy", changedBy),
            ("beforeCode", beforeStationCode),
            ("beforeOrder", beforeStationOrder),
            ("afterCode", afterStationCode),
            ("afterOrder", afterStationOrder),
            ("debugRemark", ToDbNullable(debugRemark)));
    }

    private static async Task MirrorWorkflowPassToLegacyTraceAsync(
        NpgsqlConnection connection,
        Dictionary<string, object?> workflowSerial,
        Dictionary<string, object?> station,
        string result,
        string remark,
        string changedBy,
        object? beforeStationCode,
        object? beforeStationOrder,
        object? afterStationCode,
        object? afterStationOrder,
        string nextStatus)
    {
        await EnsureSerialTrackingSchemaAsync(connection);

        var legacySerial = await GetOrCreateLegacySerialForWorkflowAsync(connection, workflowSerial);
        if (legacySerial is null)
        {
            return;
        }

        await ExecuteAsync(
            connection,
            """
            UPDATE serial_numbers
            SET status = @status,
                condition = 'Good',
                current_station_code = @stationCode,
                current_station_order = @stationOrder,
                last_moved_at = NOW(),
                updated_at = NOW()
            WHERE id = @id
            """,
            ("status", nextStatus),
            ("stationCode", afterStationCode),
            ("stationOrder", afterStationOrder),
            ("id", legacySerial["id"]));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO serial_station_logs
              (serial_id, item_id, work_order_id, station_code, station_name, action_result, remark, changed_by,
               before_station_code, before_station_order, after_station_code, after_station_order,
               station_length, pc_name, additional_info)
            VALUES
              (@serialId, @itemId, @workOrderId, @stationCode, @stationName, @result, @remark, @changedBy,
               @beforeCode, @beforeOrder, @afterCode, @afterOrder, NULL, 'K9-OPERATOR', @additionalInfo)
            """,
            ("serialId", legacySerial["id"]),
            ("itemId", legacySerial["item_id"]),
            ("workOrderId", legacySerial["work_order_id"]),
            ("stationCode", station["station_code"]),
            ("stationName", station["station_name"]),
            ("result", result),
            ("remark", remark),
            ("changedBy", changedBy),
            ("beforeCode", beforeStationCode),
            ("beforeOrder", beforeStationOrder),
            ("afterCode", afterStationCode),
            ("afterOrder", afterStationOrder),
            ("additionalInfo", remark));
    }

    private static async Task<Dictionary<string, object?>?> GetOrCreateLegacySerialForWorkflowAsync(
        NpgsqlConnection connection,
        Dictionary<string, object?> workflowSerial)
    {
        var sn = workflowSerial["sn"]?.ToString();
        var rsn = workflowSerial["rsn"]?.ToString();
        if (string.IsNullOrWhiteSpace(sn) && string.IsNullOrWhiteSpace(rsn))
        {
            return null;
        }

        var existing = await QueryRowsAsync(
            connection,
            """
            SELECT id, sn, rsn, work_order_id, item_id, item_revision_id, site_id,
                   status AS serial_status, condition, current_station_code, current_station_order
            FROM serial_numbers
            WHERE (@sn = '' OR UPPER(sn) = UPPER(@sn))
               OR (@rsn = '' OR UPPER(rsn) = UPPER(@rsn))
            ORDER BY created_at DESC, id DESC
            LIMIT 1
            """,
            ("sn", sn ?? string.Empty),
            ("rsn", rsn ?? string.Empty));
        if (existing.Count > 0)
        {
            return existing[0];
        }

        var legacyWorkOrder = await QueryRowsAsync(
            connection,
            """
            SELECT w.id AS work_order_id, w.item_id, w.item_revision_id, w.site_id
            FROM work_orders w
            JOIN items i ON i.id = w.item_id
            WHERE UPPER(w.wo) = UPPER(@wo)
              AND UPPER(i.pn) = UPPER(@pn)
            ORDER BY w.created_at DESC, w.id DESC
            LIMIT 1
            """,
            ("wo", workflowSerial["wo"]?.ToString() ?? string.Empty),
            ("pn", workflowSerial["pn"]?.ToString() ?? string.Empty));
        if (legacyWorkOrder.Count == 0 || string.IsNullOrWhiteSpace(sn))
        {
            return null;
        }

        try
        {
            var inserted = await QueryRowsAsync(
                connection,
                """
                INSERT INTO serial_numbers
                  (sn, rsn, work_order_id, item_id, item_revision_id, site_id, status, condition,
                   current_station_code, current_station_order, last_moved_at, created_at, updated_at)
                VALUES
                  (@sn, @rsn, @workOrderId, @itemId, @revisionId, @siteId, @status, @condition,
                   @stationCode, @stationOrder, @lastMovedAt, @createdAt, NOW())
                RETURNING id, sn, rsn, work_order_id, item_id, item_revision_id, site_id,
                          status AS serial_status, condition, current_station_code, current_station_order
                """,
                ("sn", sn),
                ("rsn", string.IsNullOrWhiteSpace(rsn) ? DBNull.Value : rsn),
                ("workOrderId", legacyWorkOrder[0]["work_order_id"]),
                ("itemId", legacyWorkOrder[0]["item_id"]),
                ("revisionId", legacyWorkOrder[0]["item_revision_id"]),
                ("siteId", legacyWorkOrder[0]["site_id"]),
                ("status", workflowSerial["serial_status"]),
                ("condition", workflowSerial["condition"]),
                ("stationCode", workflowSerial["current_station_code"]),
                ("stationOrder", workflowSerial["current_station_order"]),
                ("lastMovedAt", workflowSerial["last_moved_at"]),
                ("createdAt", workflowSerial["created_at"]));
            return inserted[0];
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            var retry = await QueryRowsAsync(
                connection,
                """
                SELECT id, sn, rsn, work_order_id, item_id, item_revision_id, site_id,
                       status AS serial_status, condition, current_station_code, current_station_order
                FROM serial_numbers
                WHERE UPPER(sn) = UPPER(@sn)
                ORDER BY created_at DESC, id DESC
                LIMIT 1
                """,
                ("sn", sn));
            return retry.Count == 0 ? null : retry[0];
        }
    }

    private static async Task<object> BuildTracePayloadAsync(NpgsqlConnection connection, string query, Dictionary<string, object?> serial)
    {
        var routeRows = await GetRouteRowsForItemAsync(connection, Convert.ToInt32(serial["item_id"]));
        var currentOrder = routeRows.Count == 0 ? 0 : ResolveCurrentOrder(serial, routeRows);
        var assembledParts = await GetSerialAssembledPartsAsync(connection, serial["id"]!);
        var history = await QueryRowsAsync(
            connection,
            """
            SELECT id, changed_by AS user_name, created_at AS date_time, station_code AS station,
                   station_length AS length, pc_name, action_result AS result,
                   COALESCE(additional_info, remark, '') AS additional_info
            FROM serial_station_logs
            WHERE serial_id = @serialId
              AND UPPER(action_result) IN ('PASS', 'FAIL')
            ORDER BY created_at DESC, id DESC
            LIMIT 300
            """,
            ("serialId", serial["id"]));
        history.Add(BuildSerialGeneratedHistoryRow(serial));

        var routing = routeRows.Select(step =>
        {
            var order = Convert.ToInt32(step["station_order"]);
            var isSerialCompleted = string.Equals(serial["serial_status"]?.ToString(), "Completed", StringComparison.OrdinalIgnoreCase);
            var state = currentOrder == 0
                ? "pending"
                : isSerialCompleted && order <= currentOrder
                    ? "completed"
                    : order < currentOrder ? "completed" : order == currentOrder ? "current" : "pending";
            return new Dictionary<string, object?>
            {
                ["station_order"] = order,
                ["station_code"] = step["station_code"],
                ["station_name"] = step["station_name"],
                ["sample_mode"] = step["sample_mode"],
                ["report_mode"] = step["report_mode"],
                ["station_login_id"] = step["station_login_id"],
                ["state"] = state,
                ["is_current"] = state == "current"
            };
        }).ToList();

        var completed = routing.Count(row => row["state"]?.ToString() == "completed");
        var current = routing.FirstOrDefault(row => row["is_current"] is true);
        var pending = Math.Max(routing.Count - completed - (current is null ? 0 : 1), 0);
        var percent = routing.Count > 0 ? (int)Math.Round(((completed + (current is null ? 0 : 1)) / (double)routing.Count) * 100) : 0;

        return new
        {
            query,
            matched_by = string.Equals(serial["sn"]?.ToString(), query, StringComparison.OrdinalIgnoreCase) ? "SN" : "RSN",
            serial = new
            {
                id = serial["id"],
                sn = serial["sn"],
                rsn = serial["rsn"],
                status = serial["serial_status"],
                condition = serial["condition"],
                current_station_code = current?["station_code"] ?? serial["current_station_code"],
                current_station_name = current?["station_name"],
                current_station_order = current?["station_order"] ?? (currentOrder == 0 ? null : currentOrder),
                created_at = serial["created_at"],
                updated_at = serial["updated_at"],
                last_moved_at = serial["last_moved_at"]
            },
            device = new
            {
                product_line = serial["product_line_name"] ?? serial["product_line_code"] ?? "-",
                pn = serial["pn"],
                revision = serial["revision"] ?? "-",
                work_order = serial["wo"],
                work_order_status = serial["wo_status"],
                work_order_qty = serial["wo_qty"],
                work_order_balance = serial["wo_balance"],
                plant = serial["plant"],
                site = serial["site_name"] ?? "-",
                description = serial["item_description"] ?? "-"
            },
            progress = new { total = routing.Count, completed, current = current is null ? 0 : 1, pending, percent },
            routing,
            history,
            assembled_parts = assembledParts,
            generated_at = DateTime.UtcNow
        };
    }

    private static async Task<object> BuildWorkflowTracePayloadAsync(NpgsqlConnection connection, string query, Dictionary<string, object?> serial)
    {
        var routeRows = await GetWorkflowRouteRowsForPartAsync(connection, Convert.ToInt32(serial["workflow_part_id"]));
        var currentOrder = routeRows.Count == 0 ? 0 : ResolveCurrentOrder(serial, routeRows);
        var multiboxRows = await QueryRowsAsync(
            connection,
            """
            SELECT b.box_no, p.pallet_no, s.shipment_no
            FROM workflow_multibox_items i
            JOIN workflow_multiboxes b ON b.id = i.box_id
            LEFT JOIN workflow_pallet_items pi ON pi.box_id = b.id
            LEFT JOIN workflow_pallets p ON p.id = pi.pallet_id
            LEFT JOIN workflow_shipment_items si ON si.pallet_id = p.id
            LEFT JOIN workflow_shipments s ON s.id = si.shipment_id
            WHERE i.workflow_serial_id = @serialId
            ORDER BY i.added_at DESC, i.id DESC
            LIMIT 1
            """,
            ("serialId", serial["id"]));
        var multiboxNo = multiboxRows.Count > 0 ? multiboxRows[0]["box_no"] : null;
        var palletNo = multiboxRows.Count > 0 ? multiboxRows[0]["pallet_no"] : null;
        var shipmentNo = multiboxRows.Count > 0 ? multiboxRows[0]["shipment_no"] : null;
        var assembledParts = await GetWorkflowSerialAssembledPartsAsync(connection, serial["id"]!);
        await EnsureSerialExternalValuesTableAsync(connection);
        var snValues = await GetSerialExternalValuesAsync(connection, serial["id"]!);
        var history = await QueryRowsAsync(
            connection,
            """
            SELECT id, changed_by AS user_name, created_at AS date_time, station_code AS station,
                   NULL::text AS length, NULL::text AS pc_name, action_result AS result,
                   COALESCE(remark, '') AS additional_info
            FROM workflow_serial_station_logs
            WHERE workflow_serial_id = @serialId
              AND UPPER(action_result) IN ('PASS', 'FAIL')
            ORDER BY created_at DESC, id DESC
            LIMIT 300
            """,
            ("serialId", serial["id"]));
        history.AddRange(await GetWorkflowBomBindingHistoryRowsAsync(connection, serial["id"]!));
        history.Add(BuildSerialGeneratedHistoryRow(serial));
        history = history
            .OrderByDescending(row => row["date_time"] is DateTime dateTime ? dateTime : DateTime.MinValue)
            .ThenByDescending(row => Convert.ToInt64(row["id"] ?? 0))
            .ToList();

        var routing = routeRows.Select(step =>
        {
            var order = Convert.ToInt32(step["station_order"]);
            var isSerialCompleted = string.Equals(serial["serial_status"]?.ToString(), "Completed", StringComparison.OrdinalIgnoreCase);
            var state = currentOrder == 0
                ? "pending"
                : isSerialCompleted && order <= currentOrder
                    ? "completed"
                    : order < currentOrder ? "completed" : order == currentOrder ? "current" : "pending";
            return new Dictionary<string, object?>
            {
                ["station_order"] = order,
                ["station_code"] = step["station_code"],
                ["station_name"] = step["station_name"],
                ["sample_mode"] = step["sample_mode"],
                ["report_mode"] = step["report_mode"],
                ["station_login_id"] = step["station_login_id"],
                ["state"] = state,
                ["is_current"] = state == "current"
            };
        }).ToList();

        var completed = routing.Count(row => row["state"]?.ToString() == "completed");
        var current = routing.FirstOrDefault(row => row["is_current"] is true);
        var pending = Math.Max(routing.Count - completed - (current is null ? 0 : 1), 0);
        var percent = routing.Count > 0 ? (int)Math.Round(((completed + (current is null ? 0 : 1)) / (double)routing.Count) * 100) : 0;

        return new
        {
            query,
            matched_by = string.Equals(serial["sn"]?.ToString(), query, StringComparison.OrdinalIgnoreCase) ? "SN" : "RSN",
            serial = new
            {
                id = serial["id"],
                sn = serial["sn"],
                rsn = serial["rsn"],
                status = serial["serial_status"],
                condition = serial["condition"],
                current_station_code = current?["station_code"] ?? serial["current_station_code"],
                current_station_name = current?["station_name"],
                current_station_order = current?["station_order"] ?? (currentOrder == 0 ? null : currentOrder),
                created_at = serial["created_at"],
                updated_at = serial["updated_at"],
                last_moved_at = serial["last_moved_at"],
                multibox_no = multiboxNo,
                pallet_no = palletNo,
                shipment_no = shipmentNo
            },
            device = new
            {
                product_line = serial["product_line_name"] ?? "-",
                pn = serial["pn"],
                revision = serial["revision"] ?? "-",
                work_order = serial["wo"],
                work_order_status = serial["wo_status"],
                work_order_qty = serial["wo_qty"],
                work_order_balance = serial["wo_balance"],
                plant = serial["plant"],
                site = serial["site_name"] ?? "-",
                description = serial["item_description"] ?? "-"
            },
            progress = new { total = routing.Count, completed, current = current is null ? 0 : 1, pending, percent },
            routing,
            history,
            sn_values = snValues,
            assembled_parts = assembledParts,
            generated_at = DateTime.UtcNow
        };
    }

    private static async Task<List<Dictionary<string, object?>>> GetSerialAssembledPartsAsync(
        NpgsqlConnection connection,
        object serialId)
    {
        return await QueryRowsAsync(
            connection,
            """
            SELECT child_item.pn,
                   child.sn AS son_sn,
                   COALESCE(pt.code, pt.type, '') AS pn_type,
                   l.station_code,
                   ''::text AS station_name
            FROM serial_assembly_links l
            JOIN serial_numbers child ON child.id = l.child_serial_id
            JOIN items child_item ON child_item.id = child.item_id
            LEFT JOIN pn_types pt ON pt.id = child_item.pn_type_id
            WHERE l.parent_serial_id = @serialId
            ORDER BY l.created_at DESC, l.id DESC
            """,
            ("serialId", serialId));
    }

    private static async Task<List<Dictionary<string, object?>>> GetWorkflowSerialAssembledPartsAsync(
        NpgsqlConnection connection,
        object workflowSerialId)
    {
        return await QueryRowsAsync(
            connection,
            """
            SELECT child_part.pn,
                   child.sn AS son_sn,
                   COALESCE(pt.code, pt.type, '') AS pn_type,
                   l.station_code,
                   COALESCE(l.station_name, child_bom.station_name, '') AS station_name
            FROM workflow_serial_bom_bindings l
            JOIN workflow_serial_numbers child ON child.id = l.child_workflow_serial_id
            JOIN workflow_part_numbers child_part ON child_part.id = child.workflow_part_id
            LEFT JOIN workflow_bom_children child_bom ON child_bom.id = l.workflow_bom_child_id
            LEFT JOIN pn_types pt ON pt.id = child_part.pn_type_id
            WHERE l.parent_workflow_serial_id = @serialId
            ORDER BY l.created_at DESC, l.id DESC
            """,
            ("serialId", workflowSerialId));
    }

    private static async Task<List<Dictionary<string, object?>>> GetWorkflowBomBindingHistoryRowsAsync(
        NpgsqlConnection connection,
        object workflowSerialId)
    {
        return await QueryRowsAsync(
            connection,
            """
            SELECT l.id,
                   l.created_by AS user_name,
                   l.created_at AS date_time,
                   l.station_code AS station,
                   NULL::text AS length,
                   NULL::text AS pc_name,
                   ''::text AS result,
                   CASE
                     WHEN l.parent_workflow_serial_id = @serialId
                       THEN 'Bound child ' || child.rsn || ' (' || child_part.pn || ')'
                     ELSE 'Bound to parent ' || parent_sn.rsn || ' (' || parent_part.pn || ')'
                   END AS additional_info,
                   'BOM_BIND'::text AS event_type,
                   child.sn AS child_sn,
                   child.rsn AS child_rsn,
                   child_part.pn AS child_pn,
                   COALESCE(child_wo.revision, '-') AS child_revision,
                   parent_sn.sn AS parent_sn,
                   parent_sn.rsn AS parent_rsn,
                   parent_part.pn AS parent_pn,
                   COALESCE(parent_wo.revision, '-') AS parent_revision
            FROM workflow_serial_bom_bindings l
            JOIN workflow_serial_numbers parent_sn ON parent_sn.id = l.parent_workflow_serial_id
            JOIN workflow_part_numbers parent_part ON parent_part.id = parent_sn.workflow_part_id
            LEFT JOIN workflow_work_orders parent_wo ON parent_wo.id = parent_sn.workflow_work_order_id
            JOIN workflow_serial_numbers child ON child.id = l.child_workflow_serial_id
            JOIN workflow_part_numbers child_part ON child_part.id = child.workflow_part_id
            LEFT JOIN workflow_work_orders child_wo ON child_wo.id = child.workflow_work_order_id
            WHERE l.parent_workflow_serial_id = @serialId
               OR l.child_workflow_serial_id = @serialId
            ORDER BY l.created_at DESC, l.id DESC
            LIMIT 300
            """,
            ("serialId", workflowSerialId));
    }

    private static Dictionary<string, object?> BuildSerialGeneratedHistoryRow(Dictionary<string, object?> serial)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = 0,
            ["user_name"] = "system",
            ["date_time"] = serial["created_at"],
            ["station"] = serial["current_station_code"],
            ["length"] = null,
            ["pc_name"] = null,
            ["result"] = "",
            ["additional_info"] = "SN generated",
            ["event_type"] = "SN_GENERATED"
        };
    }

                                        private static async Task<object?> GetWorkflowSnapshotAsync(NpgsqlConnection connection, string pn, string? wo = null)
    {
        await EnsureWorkflowSchemaAsync(connection);
        await EnsureRoutingStepLoginColumnsAsync(connection);

        var partRows = await QueryRowsAsync(
            connection,
            """
            SELECT
              p.id,
              p.pn,
              p.description,
              p.sgd_control,
              p.item_type,
              p.box_qty,
              COALESCE(st.sn_type_name, p.sn_type_name, '') AS sn_type_name,
              p.pn_type_id,
              p.created_at,
              p.updated_at
            FROM workflow_part_numbers p
            LEFT JOIN sn_types st ON st.id = p.sn_type_id
            WHERE p.pn = @pn
            LIMIT 1
            """,
            ("pn", pn));

        if (partRows.Count > 0)
        {
            var part = partRows[0];
            var workflowPartId = Convert.ToInt32(part["id"]);
            var workOrderRows = await QueryRowsAsync(
                connection,
                """
                SELECT
                  wo,
                  plant,
                  site_id,
                  site_name,
                  due_date,
                  qty,
                  status,
                  @pn AS pn,
                  revision,
                  lot,
                  created_at,
                  updated_at
                FROM workflow_work_orders
                WHERE workflow_part_id = @workflowPartId
                  AND (@wo = '' OR UPPER(wo) = UPPER(@wo))
                ORDER BY updated_at DESC, id DESC
                LIMIT 1
                """,
                ("workflowPartId", workflowPartId),
                ("pn", pn),
                ("wo", string.IsNullOrWhiteSpace(wo) ? string.Empty : wo.Trim()));

            var routingRows = await QueryRowsAsync(
                connection,
                """
                SELECT
                  r.id,
                  r.station_order,
                  r.station_code,
                  r.station_name,
                  r.sample_mode,
                  r.report_mode,
                  r.station_login_id,
                  r.station_login_password,
                  r.station_ip,
                  r.printer_ip,
                  COALESCE(ps.status, r.preview_status) AS preview_status
                FROM workflow_routing_steps r
                LEFT JOIN workflow_preview_station_statuses ps
                  ON ps.workflow_part_id = r.workflow_part_id
                 AND ps.station_code = r.station_code
                WHERE r.workflow_part_id = @workflowPartId
                ORDER BY r.station_order ASC, r.id ASC
                """,
                ("workflowPartId", workflowPartId));

            var bomRows = await QueryRowsAsync(
                connection,
                """
                SELECT id, son_pn, son_description, station_code, station_name, item_type, qty
                FROM workflow_bom_children
                WHERE workflow_part_id = @workflowPartId
                ORDER BY id ASC
                """,
                ("workflowPartId", workflowPartId));

            var ruleRows = await QueryRowsAsync(
                connection,
                """
                SELECT station_code, rule_text
                FROM workflow_station_rules
                WHERE workflow_part_id = @workflowPartId
                ORDER BY station_code ASC, rule_order ASC, id ASC
                """,
                ("workflowPartId", workflowPartId));

            var labelPrintingRows = await GetWorkflowStationLabelPrintingRowsAsync(connection, workflowPartId);
            var weighingRows = await GetWorkflowStationWeighingRowsAsync(connection, workflowPartId);
            var samplingRows = await GetWorkflowStationSamplingRowsAsync(connection, workflowPartId);
            var repairRows = await GetWorkflowStationRepairRowsAsync(connection, workflowPartId);

            var statusRows = await QueryRowsAsync(
                connection,
                """
                SELECT station_code, status
                FROM workflow_preview_station_statuses
                WHERE workflow_part_id = @workflowPartId
                """,
                ("workflowPartId", workflowPartId));

            return new
            {
                partNumber = new
                {
                    pn = part["pn"],
                    description = part["description"],
                    sgd_control = part["sgd_control"],
                    item_type = part["item_type"],
                    sn_type_name = part["sn_type_name"],
                    pn_type_id = part["pn_type_id"]
                },
                workOrder = workOrderRows.Count > 0 ? workOrderRows[0] : null,
                routing = routingRows,
                bom = bomRows,
                stationRules = GroupWorkflowRules(ruleRows),
                stationLabelPrinting = GroupWorkflowStationLabelPrinting(labelPrintingRows),
                stationWeighing = GroupWorkflowStationWeighing(weighingRows),
                stationSampling = GroupWorkflowStationSampling(samplingRows),
                stationRepair = GroupWorkflowStationRepair(repairRows),
                previewStatuses = statusRows.ToDictionary(
                    row => Convert.ToString(row["station_code"]) ?? string.Empty,
                    row => Convert.ToString(row["status"]) ?? string.Empty)
            };
        }

        var itemRows = await QueryRowsAsync(
            connection,
            """
            SELECT
              i.id,
              i.pn,
              i.description,
              i.sgd_control,
              i.item_type,
              COALESCE(st.sn_type_name, '') AS sn_type_name,
              i.pn_type_id
            FROM items i
            LEFT JOIN sn_types st ON st.id = i.sn_type_id
            WHERE i.pn = @pn
            LIMIT 1
            """,
            ("pn", pn));

        if (itemRows.Count == 0)
        {
            return null;
        }

        var item = itemRows[0];
        var itemId = Convert.ToInt32(item["id"]);
        var existingWorkOrderRows = await QueryRowsAsync(
            connection,
            """
            SELECT
              w.wo,
              '' AS plant,
              w.site_id,
              s.name AS site_name,
              w.due_date,
              w.qty,
              w.status,
              @pn AS pn,
              ir.revision,
              w.lot,
              w.created_at,
              w.updated_at
            FROM work_orders w
            JOIN sites s ON s.id = w.site_id
            JOIN item_revisions ir ON ir.id = w.item_revision_id
            WHERE w.item_id = @itemId
              AND (@wo = '' OR UPPER(w.wo) = UPPER(@wo))
            ORDER BY w.updated_at DESC, w.id DESC
            LIMIT 1
            """,
            ("itemId", itemId),
            ("pn", pn),
            ("wo", string.IsNullOrWhiteSpace(wo) ? string.Empty : wo.Trim()));

        var existingRoutingRows = await QueryRowsAsync(
            connection,
            """
            SELECT id, station_order, station_code, station_name, sample_mode, report_mode,
                   station_login_id, station_login_password, station_ip, printer_ip,
                   NULL::varchar AS preview_status
            FROM item_routing_steps
            WHERE item_id = @itemId
            ORDER BY station_order ASC, id ASC
            """,
            ("itemId", itemId));

        var existingBomRows = await QueryRowsAsync(
            connection,
            """
            SELECT
              bl.id,
              son.pn AS son_pn,
              son.description AS son_description,
              '' AS station_code,
              '' AS station_name,
              son.item_type AS item_type,
              COALESCE(pt.code, '') AS pn_type,
              bl.qty
            FROM item_bom_lines bl
            JOIN items son ON son.id = bl.son_item_id
            LEFT JOIN pn_types pt ON pt.id = son.pn_type_id
            WHERE bl.main_item_id = @itemId
            ORDER BY bl.id ASC
            """,
            ("itemId", itemId));

        return new
        {
            partNumber = new
            {
                pn = item["pn"],
                description = item["description"],
                sgd_control = item["sgd_control"],
                item_type = item["item_type"],
                sn_type_name = item["sn_type_name"],
                pn_type_id = item["pn_type_id"]
            },
            workOrder = existingWorkOrderRows.Count > 0 ? existingWorkOrderRows[0] : null,
            routing = existingRoutingRows,
            bom = existingBomRows,
            stationRules = new Dictionary<string, List<string>>(),
            stationLabelPrinting = new Dictionary<string, object>(),
            stationWeighing = new Dictionary<string, object>(),
            stationSampling = new Dictionary<string, object>(),
            stationRepair = new Dictionary<string, object>(),
            previewStatuses = new Dictionary<string, string>()
        };
    }

    private static Dictionary<string, List<string>> GroupWorkflowRules(List<Dictionary<string, object?>> rows)
    {
        var grouped = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var stationCode = Convert.ToString(row["station_code"]) ?? string.Empty;
            var ruleText = Convert.ToString(row["rule_text"]) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(stationCode) || string.IsNullOrWhiteSpace(ruleText))
            {
                continue;
            }

            if (!grouped.TryGetValue(stationCode, out var rules))
            {
                rules = new List<string>();
                grouped[stationCode] = rules;
            }

            rules.Add(ruleText);
        }

        return grouped;
    }

    private static async Task<List<Dictionary<string, object?>>> GetWorkflowStationLabelPrintingRowsAsync(NpgsqlConnection connection, int workflowPartId)
    {
        return await QueryRowsAsync(
            connection,
            """
            SELECT
              lp.station_code,
              COALESCE(lp.station_id, r.id) AS station_id,
              COALESCE(NULLIF(lp.station_name, ''), r.station_name, '') AS station_name,
              COALESCE(lp.label_code, '') AS label_code,
              COALESCE(lp.label_description, '') AS label_description,
              COALESCE(lp.printer_id, '') AS printer_id,
              COALESCE(lp.printer_name, '') AS printer_name,
              COALESCE(lp.ip_address, '') AS ip_address,
              COALESCE(lp.port, '') AS port,
              COALESCE(lp.status, '') AS status,
              COALESCE(lp.is_label_printing_enabled, FALSE) AS is_label_printing_enabled
            FROM workflow_station_label_printing lp
            LEFT JOIN workflow_routing_steps r
              ON r.workflow_part_id = lp.workflow_part_id
             AND r.station_code = lp.station_code
            WHERE lp.workflow_part_id = @workflowPartId
            ORDER BY lp.station_code ASC
            """,
            ("workflowPartId", workflowPartId));
    }

    private static Dictionary<string, object> GroupWorkflowStationLabelPrinting(List<Dictionary<string, object?>> rows)
    {
        return rows.ToDictionary(
            row => Convert.ToString(row["station_code"]) ?? string.Empty,
            row => (object)BuildWorkflowStationLabelPrintingConfig(row),
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object?> BuildWorkflowStationLabelPrintingConfig(Dictionary<string, object?> row)
    {
        return new Dictionary<string, object?>
        {
            ["stationId"] = row["station_id"] is null || row["station_id"] is DBNull ? null : Convert.ToInt32(row["station_id"]),
            ["stationName"] = Convert.ToString(row["station_name"]) ?? string.Empty,
            ["labelCode"] = Convert.ToString(row["label_code"]) ?? string.Empty,
            ["labelDescription"] = Convert.ToString(row["label_description"]) ?? string.Empty,
            ["printerId"] = Convert.ToString(row["printer_id"]) ?? string.Empty,
            ["printerName"] = Convert.ToString(row["printer_name"]) ?? string.Empty,
            ["ipAddress"] = Convert.ToString(row["ip_address"]) ?? string.Empty,
            ["port"] = Convert.ToString(row["port"]) ?? string.Empty,
            ["status"] = Convert.ToString(row["status"]) ?? string.Empty,
            ["isLabelPrintingEnabled"] = row["is_label_printing_enabled"] is bool enabled && enabled
        };
    }

    private static async Task<List<Dictionary<string, object?>>> GetWorkflowStationWeighingRowsAsync(NpgsqlConnection connection, int workflowPartId)
    {
        return await QueryRowsAsync(
            connection,
            """
            SELECT
              w.station_code,
              COALESCE(w.station_id, r.id) AS station_id,
              COALESCE(NULLIF(w.station_name, ''), r.station_name, '') AS station_name,
              COALESCE(w.minimum_weight::text, '') AS minimum_weight,
              COALESCE(w.maximum_weight::text, '') AS maximum_weight,
              COALESCE(w.tolerance::text, '') AS tolerance,
              COALESCE(w.is_weighing_enabled, FALSE) AS is_weighing_enabled
            FROM workflow_station_weighing w
            LEFT JOIN workflow_routing_steps r
              ON r.workflow_part_id = w.workflow_part_id
             AND r.station_code = w.station_code
            WHERE w.workflow_part_id = @workflowPartId
            ORDER BY w.station_code ASC
            """,
            ("workflowPartId", workflowPartId));
    }

    private static Dictionary<string, object> GroupWorkflowStationWeighing(List<Dictionary<string, object?>> rows)
    {
        return rows.ToDictionary(
            row => Convert.ToString(row["station_code"]) ?? string.Empty,
            row => (object)BuildWorkflowStationWeighingConfig(row),
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object?> BuildWorkflowStationWeighingConfig(Dictionary<string, object?> row)
    {
        return new Dictionary<string, object?>
        {
            ["stationId"] = row["station_id"] is null || row["station_id"] is DBNull ? null : Convert.ToInt32(row["station_id"]),
            ["stationName"] = Convert.ToString(row["station_name"]) ?? string.Empty,
            ["minimumWeight"] = Convert.ToString(row["minimum_weight"]) ?? string.Empty,
            ["maximumWeight"] = Convert.ToString(row["maximum_weight"]) ?? string.Empty,
            ["tolerance"] = Convert.ToString(row["tolerance"]) ?? string.Empty,
            ["isWeighingEnabled"] = row["is_weighing_enabled"] is bool enabled && enabled
        };
    }

    private static async Task<List<Dictionary<string, object?>>> GetWorkflowStationSamplingRowsAsync(NpgsqlConnection connection, int workflowPartId)
    {
        return await QueryRowsAsync(
            connection,
            """
            SELECT
              s.station_code,
              COALESCE(s.station_id, r.id) AS station_id,
              COALESCE(NULLIF(s.station_name, ''), r.station_name, '') AS station_name,
              COALESCE(s.sampling_type, 'PERIODIC') AS sampling_type,
              COALESCE(s.interval_qty::text, '10') AS interval_qty,
              COALESCE(s.sample_qty::text, '1') AS sample_qty,
              COALESCE(s.lot_size::text, '1000') AS lot_size,
              COALESCE(s.is_sampling_enabled, FALSE) AS is_sampling_enabled
            FROM workflow_station_sampling s
            LEFT JOIN workflow_routing_steps r
              ON r.workflow_part_id = s.workflow_part_id
             AND r.station_code = s.station_code
            WHERE s.workflow_part_id = @workflowPartId
            ORDER BY s.station_code ASC
            """,
            ("workflowPartId", workflowPartId));
    }

    private static Dictionary<string, object> GroupWorkflowStationSampling(List<Dictionary<string, object?>> rows)
    {
        return rows.ToDictionary(
            row => Convert.ToString(row["station_code"]) ?? string.Empty,
            row => (object)new Dictionary<string, object?>
            {
                ["stationId"] = row["station_id"] is null || row["station_id"] is DBNull ? null : Convert.ToInt32(row["station_id"]),
                ["stationName"] = Convert.ToString(row["station_name"]) ?? string.Empty,
                ["samplingType"] = Convert.ToString(row["sampling_type"]) ?? "PERIODIC",
                ["intervalQty"] = Convert.ToString(row["interval_qty"]) ?? "10",
                ["sampleQty"] = Convert.ToString(row["sample_qty"]) ?? "1",
                ["lotSize"] = Convert.ToString(row["lot_size"]) ?? "1000",
                ["isSamplingEnabled"] = row["is_sampling_enabled"] is bool enabled && enabled
            },
            StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<List<Dictionary<string, object?>>> GetWorkflowStationRepairRowsAsync(NpgsqlConnection connection, int workflowPartId)
    {
        return await QueryRowsAsync(
            connection,
            """
            SELECT
              rp.station_code,
              COALESCE(rp.station_id, r.id) AS station_id,
              COALESCE(NULLIF(rp.station_name, ''), r.station_name, '') AS station_name,
              COALESCE(rp.repair_station_name, '') AS repair_station_name,
              COALESCE(rp.is_repair_station_enabled, FALSE) AS is_repair_station_enabled
            FROM workflow_station_repair rp
            LEFT JOIN workflow_routing_steps r
              ON r.workflow_part_id = rp.workflow_part_id
             AND r.station_code = rp.station_code
            WHERE rp.workflow_part_id = @workflowPartId
            ORDER BY rp.station_code ASC
            """,
            ("workflowPartId", workflowPartId));
    }

    private static Dictionary<string, object> GroupWorkflowStationRepair(List<Dictionary<string, object?>> rows)
    {
        return rows.ToDictionary(
            row => Convert.ToString(row["station_code"]) ?? string.Empty,
            row => (object)new Dictionary<string, object?>
            {
                ["stationId"] = row["station_id"] is null || row["station_id"] is DBNull ? null : Convert.ToInt32(row["station_id"]),
                ["stationName"] = Convert.ToString(row["station_name"]) ?? string.Empty,
                ["repairStationName"] = Convert.ToString(row["repair_station_name"]) ?? string.Empty,
                ["isRepairStationEnabled"] = row["is_repair_station_enabled"] is bool enabled && enabled
            },
            StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<Dictionary<string, object?>?> GetWorkflowStationWeighingConfigAsync(
        NpgsqlConnection connection,
        int workflowPartId,
        string? stationCode)
    {
        if (string.IsNullOrWhiteSpace(stationCode))
        {
            return null;
        }

        var rows = await QueryRowsAsync(
            connection,
            """
            SELECT
              w.station_code,
              COALESCE(w.station_id, r.id) AS station_id,
              COALESCE(NULLIF(w.station_name, ''), r.station_name, '') AS station_name,
              COALESCE(w.minimum_weight::text, '') AS minimum_weight,
              COALESCE(w.maximum_weight::text, '') AS maximum_weight,
              COALESCE(w.tolerance::text, '') AS tolerance,
              COALESCE(w.is_weighing_enabled, FALSE) AS is_weighing_enabled
            FROM workflow_station_weighing w
            LEFT JOIN workflow_routing_steps r
              ON r.workflow_part_id = w.workflow_part_id
             AND r.station_code = w.station_code
            WHERE w.workflow_part_id = @workflowPartId
              AND UPPER(w.station_code) = UPPER(@stationCode)
            LIMIT 1
            """,
            ("workflowPartId", workflowPartId),
            ("stationCode", stationCode.Trim()));

        return rows.Count > 0 ? BuildWorkflowStationWeighingConfig(rows[0]) : null;
    }

    private static async Task<Dictionary<string, object?>?> GetWorkflowStationLabelPrintingConfigAsync(
        NpgsqlConnection connection,
        int workflowPartId,
        string? stationCode)
    {
        if (string.IsNullOrWhiteSpace(stationCode))
        {
            return null;
        }

        var rows = await QueryRowsAsync(
            connection,
            """
            SELECT
              lp.station_code,
              COALESCE(lp.station_id, r.id) AS station_id,
              COALESCE(NULLIF(lp.station_name, ''), r.station_name, '') AS station_name,
              COALESCE(lp.label_code, '') AS label_code,
              COALESCE(lp.label_description, '') AS label_description,
              COALESCE(lp.printer_id, '') AS printer_id,
              COALESCE(lp.printer_name, '') AS printer_name,
              COALESCE(lp.ip_address, '') AS ip_address,
              COALESCE(lp.port, '') AS port,
              COALESCE(lp.status, '') AS status,
              COALESCE(lp.is_label_printing_enabled, FALSE) AS is_label_printing_enabled
            FROM workflow_station_label_printing lp
            LEFT JOIN workflow_routing_steps r
              ON r.workflow_part_id = lp.workflow_part_id
             AND r.station_code = lp.station_code
            WHERE lp.workflow_part_id = @workflowPartId
              AND UPPER(lp.station_code) = UPPER(@stationCode)
            LIMIT 1
            """,
            ("workflowPartId", workflowPartId),
            ("stationCode", stationCode.Trim()));

        return rows.Count == 0 ? null : BuildWorkflowStationLabelPrintingConfig(rows[0]);
    }

    private static Dictionary<string, object?> BuildOperatorLabelPrintingResponse(
        Dictionary<string, object?> config,
        Dictionary<string, object?> station,
        string message = "",
        bool? success = null)
    {
        var response = new Dictionary<string, object?>(config, StringComparer.OrdinalIgnoreCase)
        {
            ["stationCode"] = Convert.ToString(station["station_code"]) ?? string.Empty,
            ["stationName"] = Convert.ToString(station["station_name"]) ?? string.Empty,
            ["workflowPartId"] = station["workflow_part_id"],
            ["message"] = message
        };

        if (success.HasValue)
        {
            response["success"] = success.Value;
        }

        return response;
    }

    private static async Task UpdateWorkflowStationPrinterAsync(
        NpgsqlConnection connection,
        int workflowPartId,
        string? stationCode,
        string printerIp,
        string port,
        string? status = null)
    {
        if (string.IsNullOrWhiteSpace(stationCode))
        {
            return;
        }

        if (status is null)
        {
            await ExecuteAsync(
                connection,
                """
                UPDATE workflow_station_label_printing
                SET printer_id = @printerIp,
                    printer_name = @printerIp,
                    ip_address = @printerIp,
                    port = @port,
                    updated_at = NOW()
                WHERE workflow_part_id = @workflowPartId
                  AND UPPER(station_code) = UPPER(@stationCode)
                  AND is_label_printing_enabled = TRUE
                """,
                ("workflowPartId", workflowPartId),
                ("stationCode", stationCode),
                ("printerIp", printerIp.Trim()),
                ("port", port.Trim()));
            return;
        }

        await ExecuteAsync(
            connection,
            """
            UPDATE workflow_station_label_printing
            SET printer_id = @printerIp,
                printer_name = @printerIp,
                ip_address = @printerIp,
                port = @port,
                status = @status,
                updated_at = NOW()
            WHERE workflow_part_id = @workflowPartId
              AND UPPER(station_code) = UPPER(@stationCode)
              AND is_label_printing_enabled = TRUE
            """,
            ("workflowPartId", workflowPartId),
            ("stationCode", stationCode),
            ("printerIp", printerIp.Trim()),
            ("port", port.Trim()),
            ("status", status));
    }

    private static async Task TestPrinterConnectionAsync(string printerIp, int printerPort)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var client = new TcpClient();
        await client.ConnectAsync(printerIp, printerPort).WaitAsync(timeout.Token);
    }

    private static Dictionary<string, object?> BuildOperatorTestLabelSerial(Dictionary<string, object?> station)
    {
        return new Dictionary<string, object?>
        {
            ["sn"] = "TEST-SERIAL",
            ["rsn"] = "TEST-SERIAL",
            ["wo"] = station.TryGetValue("wo", out var wo) ? wo : string.Empty,
            ["pn"] = station.TryGetValue("pn", out var pn) ? pn : string.Empty,
            ["revision"] = station.TryGetValue("revision", out var revision) ? revision : string.Empty,
            ["plant"] = station.TryGetValue("plant", out var plant) ? plant : string.Empty,
            ["site_name"] = station.TryGetValue("site_name", out var siteName) ? siteName : string.Empty,
            ["lot"] = station.TryGetValue("lot", out var lot) ? lot : string.Empty,
            ["item_description"] = station.TryGetValue("item_description", out var description) ? description : string.Empty,
            ["product_line_name"] = station.TryGetValue("product_line_name", out var productLine) ? productLine : string.Empty
        };
    }

    private static async Task TryPrintWorkflowStationLabelAsync(
        NpgsqlConnection connection,
        Dictionary<string, object?> serial,
        Dictionary<string, object?> station,
        Dictionary<string, object?>? labelPrinting)
    {
        try
        {
            if (labelPrinting is null ||
                !labelPrinting.TryGetValue("isLabelPrintingEnabled", out var enabledValue) ||
                enabledValue is not bool enabled ||
                !enabled)
            {
                return;
            }

            var labelCode = ReadDictionaryText(labelPrinting, "labelCode");
            var printerIp = ReadDictionaryText(labelPrinting, "ipAddress");
            if (string.IsNullOrWhiteSpace(labelCode) || string.IsNullOrWhiteSpace(printerIp))
            {
                return;
            }

            var portValue = labelPrinting.TryGetValue("port", out var configuredPort) ? configuredPort : null;
            var printerPort = ParsePositiveInt(portValue, 9100);
            var prnContent = await GetLatestLabelPrnTemplateByCodeAsync(connection, labelCode);
            if (string.IsNullOrWhiteSpace(prnContent))
            {
                return;
            }

            var renderedPrn = ApplyWorkflowLabelPlaceholders(prnContent, serial, station);
            await SendRawPrinterDataAsync(printerIp, printerPort, renderedPrn);
        }
        catch
        {
            // Printing must not change the operator station pass result.
        }
    }

    private static async Task<string?> GetLatestLabelPrnTemplateByCodeAsync(NpgsqlConnection connection, string labelCode)
    {
        var rows = await QueryRowsAsync(
            connection,
            """
            SELECT latest.prn_content
            FROM label_masters lm
            JOIN LATERAL (
              SELECT prn_content
              FROM label_prn_templates
              WHERE label_master_id = lm.id
                AND COALESCE(NULLIF(TRIM(prn_content), ''), '') <> ''
              ORDER BY version DESC, id DESC
              LIMIT 1
            ) latest ON TRUE
            WHERE UPPER(lm.label_code) = UPPER(@labelCode)
              AND COALESCE(lm.status, 'Active') = 'Active'
            LIMIT 1
            """,
            ("labelCode", labelCode.Trim()));

        return rows.Count == 0 ? null : Convert.ToString(rows[0]["prn_content"]);
    }

    private static string ApplyWorkflowLabelPlaceholders(
        string template,
        Dictionary<string, object?> serial,
        Dictionary<string, object?> station)
    {
        var rsn = ReadDictionaryText(serial, "rsn");
        var sn = ReadDictionaryText(serial, "sn");
        var serialNumber = FirstNonEmpty(rsn, sn);
        var itemDescription = ReadDictionaryText(serial, "item_description");
        var productLine = ReadDictionaryText(serial, "product_line_name");
        var stationCode = ReadDictionaryText(station, "station_code");
        var stationName = ReadDictionaryText(station, "station_name");

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RSN"] = serialNumber,
            ["SN"] = serialNumber,
            ["SERIAL"] = serialNumber,
            ["SERIALNUMBER"] = serialNumber,
            ["WO"] = ReadDictionaryText(serial, "wo"),
            ["WORKORDER"] = ReadDictionaryText(serial, "wo"),
            ["PN"] = ReadDictionaryText(serial, "pn"),
            ["PARTNUMBER"] = ReadDictionaryText(serial, "pn"),
            ["REVISION"] = ReadDictionaryText(serial, "revision"),
            ["REV"] = ReadDictionaryText(serial, "revision"),
            ["PRODUCT"] = itemDescription,
            ["PRODUCTNAME"] = itemDescription,
            ["MODEL"] = itemDescription,
            ["MODELNO"] = itemDescription,
            ["DESCRIPTION"] = itemDescription,
            ["PRODUCTDESCRIPTION"] = itemDescription,
            ["PRODUCTLINE"] = productLine,
            ["LOT"] = ReadDictionaryText(serial, "lot"),
            ["PLANT"] = ReadDictionaryText(serial, "plant"),
            ["SITE"] = ReadDictionaryText(serial, "site_name"),
            ["SITENAME"] = ReadDictionaryText(serial, "site_name"),
            ["STATION"] = FirstNonEmpty(stationCode, stationName),
            ["STATIONCODE"] = stationCode,
            ["STATIONNAME"] = stationName
        };

        return Regex.Replace(template, @"\{([^{}]+)\}", match =>
        {
            var placeholder = NormalizeLabelPlaceholderName(match.Groups[1].Value);
            return values.TryGetValue(placeholder, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : match.Value;
        });
    }

    private static async Task SendRawPrinterDataAsync(string printerIp, int printerPort, string rawData)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var client = new TcpClient();
        await client.ConnectAsync(printerIp, printerPort).WaitAsync(timeout.Token);
        using var stream = client.GetStream();
        var bytes = Encoding.UTF8.GetBytes(rawData);
        await stream.WriteAsync(bytes, timeout.Token);
        await stream.FlushAsync(timeout.Token);
    }

    private static string ReadDictionaryText(Dictionary<string, object?> row, string key)
    {
        return row.TryGetValue(key, out var value) ? Convert.ToString(value)?.Trim() ?? string.Empty : string.Empty;
    }

    private static string NormalizeLabelPlaceholderName(string value)
    {
        return Regex.Replace(value.Trim(), "[^A-Za-z0-9]", string.Empty).ToUpperInvariant();
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

                                                    private static async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task EnsureWorkflowSchemaAsync(NpgsqlConnection connection)
    {
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS public.workflow_part_numbers (
              id SERIAL PRIMARY KEY,
              pn VARCHAR(120) NOT NULL UNIQUE,
              description TEXT NOT NULL DEFAULT '',
              sgd_control BOOLEAN NOT NULL DEFAULT FALSE,
              item_type VARCHAR(40),
              sn_type_id INTEGER REFERENCES sn_types(id) ON DELETE SET NULL,
              sn_type_name VARCHAR(160),
              pn_type_id INTEGER REFERENCES pn_types(id) ON DELETE SET NULL,
              box_qty INTEGER CHECK (box_qty IS NULL OR box_qty > 0),
              created_at TIMESTAMP NOT NULL DEFAULT NOW(),
              updated_at TIMESTAMP NOT NULL DEFAULT NOW()
            )
            """);

        await ExecuteAsync(connection, "ALTER TABLE public.workflow_part_numbers ADD COLUMN IF NOT EXISTS box_qty INTEGER CHECK (box_qty IS NULL OR box_qty > 0)");

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS public.workflow_work_orders (
              id SERIAL PRIMARY KEY,
              workflow_part_id INTEGER NOT NULL REFERENCES workflow_part_numbers(id) ON DELETE CASCADE,
              wo VARCHAR(120) NOT NULL UNIQUE,
              plant VARCHAR(120),
              site_id INTEGER,
              site_name VARCHAR(160),
              due_date DATE,
              qty INTEGER CHECK (qty IS NULL OR qty > 0),
              status VARCHAR(30) NOT NULL DEFAULT 'Released',
              revision VARCHAR(80),
              lot VARCHAR(120),
              created_at TIMESTAMP NOT NULL DEFAULT NOW(),
              updated_at TIMESTAMP NOT NULL DEFAULT NOW()
            )
            """);

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS public.workflow_routing_steps (
              id SERIAL PRIMARY KEY,
              workflow_part_id INTEGER NOT NULL REFERENCES workflow_part_numbers(id) ON DELETE CASCADE,
              station_order INTEGER NOT NULL,
              station_code VARCHAR(80) NOT NULL,
              station_name VARCHAR(220) NOT NULL,
              sample_mode VARCHAR(20) NOT NULL DEFAULT 'Full',
              report_mode VARCHAR(20) NOT NULL DEFAULT 'Regular',
              preview_status VARCHAR(30),
              station_login_id VARCHAR(160),
              station_login_password VARCHAR(220),
              station_ip VARCHAR(80),
              printer_ip VARCHAR(80),
              created_at TIMESTAMP NOT NULL DEFAULT NOW(),
              updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
              CONSTRAINT uq_workflow_route_station UNIQUE (workflow_part_id, station_code)
            )
            """);

        await ExecuteAsync(connection, "ALTER TABLE public.workflow_routing_steps ADD COLUMN IF NOT EXISTS station_login_id VARCHAR(160)");
        await ExecuteAsync(connection, "ALTER TABLE public.workflow_routing_steps ADD COLUMN IF NOT EXISTS station_login_password VARCHAR(220)");
        await ExecuteAsync(connection, "ALTER TABLE public.workflow_routing_steps ADD COLUMN IF NOT EXISTS station_ip VARCHAR(80)");
        await ExecuteAsync(connection, "ALTER TABLE public.workflow_routing_steps ADD COLUMN IF NOT EXISTS printer_ip VARCHAR(80)");

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS public.workflow_bom_children (
              id SERIAL PRIMARY KEY,
              workflow_part_id INTEGER NOT NULL REFERENCES workflow_part_numbers(id) ON DELETE CASCADE,
              son_pn VARCHAR(120) NOT NULL,
              son_description TEXT NOT NULL DEFAULT '',
              station_code VARCHAR(80),
              station_name VARCHAR(220),
              item_type VARCHAR(40),
              qty INTEGER NOT NULL CHECK (qty > 0),
              created_at TIMESTAMP NOT NULL DEFAULT NOW(),
              updated_at TIMESTAMP NOT NULL DEFAULT NOW()
            )
            """);
        await ExecuteAsync(connection, "ALTER TABLE public.workflow_bom_children DROP COLUMN IF EXISTS pn_type");

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS public.workflow_station_rules (
              id SERIAL PRIMARY KEY,
              workflow_part_id INTEGER NOT NULL REFERENCES workflow_part_numbers(id) ON DELETE CASCADE,
              station_code VARCHAR(80) NOT NULL,
              station_name VARCHAR(220),
              rule_order INTEGER NOT NULL DEFAULT 10,
              rule_text TEXT NOT NULL,
              created_at TIMESTAMP NOT NULL DEFAULT NOW(),
              updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
              CONSTRAINT uq_workflow_station_rule UNIQUE (workflow_part_id, station_code, rule_order)
            )
            """);

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS public.workflow_preview_station_statuses (
              id SERIAL PRIMARY KEY,
              workflow_part_id INTEGER NOT NULL REFERENCES workflow_part_numbers(id) ON DELETE CASCADE,
              station_code VARCHAR(80) NOT NULL,
              status VARCHAR(30) NOT NULL,
              created_at TIMESTAMP NOT NULL DEFAULT NOW(),
              updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
              CONSTRAINT uq_workflow_preview_status UNIQUE (workflow_part_id, station_code)
            )
            """);

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS public.workflow_station_label_printing (
              id SERIAL PRIMARY KEY,
              workflow_part_id INTEGER NOT NULL REFERENCES workflow_part_numbers(id) ON DELETE CASCADE,
              station_code VARCHAR(80) NOT NULL,
              station_id INTEGER,
              station_name VARCHAR(220),
              label_code VARCHAR(120) NOT NULL,
              label_description TEXT,
              printer_id VARCHAR(160),
              printer_name VARCHAR(220),
              ip_address VARCHAR(80),
              port VARCHAR(20),
              status VARCHAR(30),
              is_label_printing_enabled BOOLEAN NOT NULL DEFAULT FALSE,
              created_at TIMESTAMP NOT NULL DEFAULT NOW(),
              updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
              CONSTRAINT uq_workflow_station_label_printing UNIQUE (workflow_part_id, station_code)
            )
            """);

        await ExecuteAsync(connection, "ALTER TABLE public.workflow_station_label_printing ADD COLUMN IF NOT EXISTS station_id INTEGER");
        await ExecuteAsync(connection, "ALTER TABLE public.workflow_station_label_printing ADD COLUMN IF NOT EXISTS is_label_printing_enabled BOOLEAN NOT NULL DEFAULT FALSE");

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS public.workflow_station_weighing (
              id SERIAL PRIMARY KEY,
              workflow_part_id INTEGER NOT NULL REFERENCES workflow_part_numbers(id) ON DELETE CASCADE,
              station_code VARCHAR(80) NOT NULL,
              station_id INTEGER,
              station_name VARCHAR(220),
              minimum_weight VARCHAR(80),
              maximum_weight VARCHAR(80),
              tolerance VARCHAR(80),
              is_weighing_enabled BOOLEAN NOT NULL DEFAULT FALSE,
              created_at TIMESTAMP NOT NULL DEFAULT NOW(),
              updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
              CONSTRAINT uq_workflow_station_weighing UNIQUE (workflow_part_id, station_code)
            )
            """);

        await ExecuteAsync(connection, "ALTER TABLE public.workflow_station_weighing ADD COLUMN IF NOT EXISTS station_id INTEGER");
        await ExecuteAsync(connection, "ALTER TABLE public.workflow_station_weighing ADD COLUMN IF NOT EXISTS is_weighing_enabled BOOLEAN NOT NULL DEFAULT FALSE");

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS public.workflow_station_sampling (
              id SERIAL PRIMARY KEY,
              workflow_part_id INTEGER NOT NULL REFERENCES workflow_part_numbers(id) ON DELETE CASCADE,
              station_code VARCHAR(80) NOT NULL,
              station_id INTEGER,
              station_name VARCHAR(220),
              sampling_type VARCHAR(30) NOT NULL DEFAULT 'PERIODIC',
              interval_qty INTEGER NOT NULL DEFAULT 10,
              sample_qty INTEGER NOT NULL DEFAULT 1,
              lot_size INTEGER NOT NULL DEFAULT 1000,
              is_sampling_enabled BOOLEAN NOT NULL DEFAULT FALSE,
              created_at TIMESTAMP NOT NULL DEFAULT NOW(),
              updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
              CONSTRAINT uq_workflow_station_sampling UNIQUE (workflow_part_id, station_code)
            )
            """);

        await ExecuteAsync(connection, "ALTER TABLE public.workflow_station_sampling ADD COLUMN IF NOT EXISTS station_id INTEGER");
        await ExecuteAsync(connection, "ALTER TABLE public.workflow_station_sampling ADD COLUMN IF NOT EXISTS is_sampling_enabled BOOLEAN NOT NULL DEFAULT FALSE");

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS public.workflow_station_repair (
              id SERIAL PRIMARY KEY,
              workflow_part_id INTEGER NOT NULL REFERENCES workflow_part_numbers(id) ON DELETE CASCADE,
              station_code VARCHAR(80) NOT NULL,
              station_id INTEGER,
              station_name VARCHAR(220),
              repair_station_name VARCHAR(220),
              is_repair_station_enabled BOOLEAN NOT NULL DEFAULT FALSE,
              created_at TIMESTAMP NOT NULL DEFAULT NOW(),
              updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
              CONSTRAINT uq_workflow_station_repair UNIQUE (workflow_part_id, station_code)
            )
            """);

        await ExecuteAsync(connection, "ALTER TABLE public.workflow_station_repair ADD COLUMN IF NOT EXISTS station_id INTEGER");
        await ExecuteAsync(connection, "ALTER TABLE public.workflow_station_repair ADD COLUMN IF NOT EXISTS repair_station_name VARCHAR(220)");
        await ExecuteAsync(connection, "ALTER TABLE public.workflow_station_repair ADD COLUMN IF NOT EXISTS is_repair_station_enabled BOOLEAN NOT NULL DEFAULT FALSE");

        await ExecuteAsync(connection, "CREATE SEQUENCE IF NOT EXISTS public.workflow_rsn_seq START WITH 1");
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS public.workflow_serial_numbers (
              id BIGSERIAL PRIMARY KEY,
              sn VARCHAR(220) NOT NULL UNIQUE,
              rsn VARCHAR(40) NOT NULL UNIQUE DEFAULT ('RSN' || LPAD(nextval('workflow_rsn_seq')::text, 10, '0')),
              workflow_work_order_id INTEGER NOT NULL REFERENCES workflow_work_orders(id) ON DELETE CASCADE,
              workflow_part_id INTEGER NOT NULL REFERENCES workflow_part_numbers(id) ON DELETE CASCADE,
              sn_type_id INTEGER REFERENCES sn_types(id) ON DELETE SET NULL,
              generated_index INTEGER NOT NULL,
              status VARCHAR(30) NOT NULL DEFAULT 'New',
              condition VARCHAR(30) NOT NULL DEFAULT 'Good',
              current_station_code VARCHAR(80),
              current_station_order INTEGER,
              last_moved_at TIMESTAMP,
              created_at TIMESTAMP NOT NULL DEFAULT NOW(),
              updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
              CONSTRAINT uq_workflow_serial_index UNIQUE (workflow_work_order_id, generated_index)
            )
            """);

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS public.workflow_serial_station_logs (
              id BIGSERIAL PRIMARY KEY,
              workflow_serial_id BIGINT NOT NULL REFERENCES workflow_serial_numbers(id) ON DELETE CASCADE,
              workflow_part_id INTEGER NOT NULL REFERENCES workflow_part_numbers(id) ON DELETE CASCADE,
              workflow_work_order_id INTEGER NOT NULL REFERENCES workflow_work_orders(id) ON DELETE CASCADE,
              station_code VARCHAR(80) NOT NULL,
              station_name VARCHAR(220),
              action_result VARCHAR(10) NOT NULL,
              remark TEXT,
              debug_remark TEXT,
              changed_by VARCHAR(100) NOT NULL DEFAULT 'system',
              before_station_code VARCHAR(80),
              before_station_order INTEGER,
              after_station_code VARCHAR(80),
              after_station_order INTEGER,
              created_at TIMESTAMP NOT NULL DEFAULT NOW()
            )
            """);

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS public.workflow_serial_bom_bindings (
              id BIGSERIAL PRIMARY KEY,
              parent_workflow_serial_id BIGINT NOT NULL REFERENCES workflow_serial_numbers(id) ON DELETE CASCADE,
              child_workflow_serial_id BIGINT NOT NULL REFERENCES workflow_serial_numbers(id) ON DELETE RESTRICT,
              workflow_bom_child_id INTEGER NOT NULL REFERENCES workflow_bom_children(id) ON DELETE RESTRICT,
              station_code VARCHAR(80) NOT NULL,
              station_name VARCHAR(220),
              created_by VARCHAR(100) NOT NULL DEFAULT 'system',
              created_at TIMESTAMP NOT NULL DEFAULT NOW(),
              CONSTRAINT uq_workflow_bom_child_serial UNIQUE (child_workflow_serial_id),
              CONSTRAINT uq_workflow_bom_parent_child_station UNIQUE (parent_workflow_serial_id, child_workflow_serial_id, station_code)
            )
            """);

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS public.workflow_multiboxes (
              id BIGSERIAL PRIMARY KEY,
              box_no VARCHAR(80) NOT NULL UNIQUE,
              workflow_part_id INTEGER NOT NULL REFERENCES workflow_part_numbers(id) ON DELETE CASCADE,
              workflow_work_order_id INTEGER REFERENCES workflow_work_orders(id) ON DELETE SET NULL,
              status VARCHAR(20) NOT NULL DEFAULT 'OPEN' CHECK (status IN ('OPEN', 'CLOSED')),
              created_by VARCHAR(100) NOT NULL DEFAULT 'system',
              created_at TIMESTAMP NOT NULL DEFAULT NOW(),
              updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
              closed_at TIMESTAMP
            )
            """);

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS public.workflow_multibox_items (
              id BIGSERIAL PRIMARY KEY,
              box_id BIGINT NOT NULL REFERENCES workflow_multiboxes(id) ON DELETE CASCADE,
              workflow_serial_id BIGINT NOT NULL REFERENCES workflow_serial_numbers(id) ON DELETE RESTRICT,
              added_by VARCHAR(100) NOT NULL DEFAULT 'system',
              added_at TIMESTAMP NOT NULL DEFAULT NOW(),
              CONSTRAINT uq_workflow_multibox_serial UNIQUE (workflow_serial_id),
              CONSTRAINT uq_workflow_multibox_item UNIQUE (box_id, workflow_serial_id)
            )
            """);

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS public.workflow_pallets (
              id BIGSERIAL PRIMARY KEY,
              pallet_no VARCHAR(80) NOT NULL UNIQUE,
              target_qty INTEGER NOT NULL DEFAULT 0 CHECK (target_qty >= 0),
              status VARCHAR(20) NOT NULL DEFAULT 'OPEN' CHECK (status IN ('OPEN', 'CLOSED')),
              created_by VARCHAR(100) NOT NULL DEFAULT 'system',
              created_at TIMESTAMP NOT NULL DEFAULT NOW(),
              updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
              closed_at TIMESTAMP
            )
            """);

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS public.workflow_pallet_items (
              id BIGSERIAL PRIMARY KEY,
              pallet_id BIGINT NOT NULL REFERENCES workflow_pallets(id) ON DELETE CASCADE,
              box_id BIGINT NOT NULL REFERENCES workflow_multiboxes(id) ON DELETE RESTRICT,
              added_by VARCHAR(100) NOT NULL DEFAULT 'system',
              added_at TIMESTAMP NOT NULL DEFAULT NOW(),
              CONSTRAINT uq_workflow_pallet_box UNIQUE (box_id),
              CONSTRAINT uq_workflow_pallet_item UNIQUE (pallet_id, box_id)
            )
            """);

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS public.workflow_shipments (
              id BIGSERIAL PRIMARY KEY,
              shipment_no VARCHAR(80) NOT NULL UNIQUE,
              target_qty INTEGER NOT NULL DEFAULT 0 CHECK (target_qty >= 0),
              status VARCHAR(20) NOT NULL DEFAULT 'OPEN' CHECK (status IN ('OPEN', 'CLOSED')),
              created_by VARCHAR(100) NOT NULL DEFAULT 'system',
              created_at TIMESTAMP NOT NULL DEFAULT NOW(),
              updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
              closed_at TIMESTAMP
            )
            """);

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS public.workflow_shipment_items (
              id BIGSERIAL PRIMARY KEY,
              shipment_id BIGINT NOT NULL REFERENCES workflow_shipments(id) ON DELETE CASCADE,
              pallet_id BIGINT NOT NULL REFERENCES workflow_pallets(id) ON DELETE RESTRICT,
              added_by VARCHAR(100) NOT NULL DEFAULT 'system',
              added_at TIMESTAMP NOT NULL DEFAULT NOW(),
              CONSTRAINT uq_workflow_shipment_pallet UNIQUE (pallet_id),
              CONSTRAINT uq_workflow_shipment_item UNIQUE (shipment_id, pallet_id)
            )
            """);

        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_workflow_work_orders_part ON public.workflow_work_orders (workflow_part_id)");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_workflow_route_part ON public.workflow_routing_steps (workflow_part_id, station_order)");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_workflow_bom_part ON public.workflow_bom_children (workflow_part_id)");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_workflow_rules_part ON public.workflow_station_rules (workflow_part_id, station_code)");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_workflow_label_printing_part ON public.workflow_station_label_printing (workflow_part_id, station_code)");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_workflow_serials_wo ON public.workflow_serial_numbers (workflow_work_order_id)");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_workflow_serials_part ON public.workflow_serial_numbers (workflow_part_id)");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_workflow_bom_bind_parent_station ON public.workflow_serial_bom_bindings (parent_workflow_serial_id, station_code)");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_workflow_bom_bind_child ON public.workflow_serial_bom_bindings (child_workflow_serial_id)");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_workflow_station_logs_serial ON public.workflow_serial_station_logs (workflow_serial_id, created_at DESC)");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_workflow_station_logs_station ON public.workflow_serial_station_logs (workflow_part_id, station_code)");
        await ExecuteAsync(connection, "ALTER TABLE public.workflow_serial_station_logs ADD COLUMN IF NOT EXISTS debug_remark TEXT");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_workflow_multiboxes_open ON public.workflow_multiboxes (workflow_part_id, workflow_work_order_id, status)");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_workflow_pallets_open ON public.workflow_pallets (created_by, status)");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_workflow_shipments_open ON public.workflow_shipments (created_by, status)");
    }

    private static async Task EnsureWorkflowStationLoginsTableAsync(NpgsqlConnection connection)
    {
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS public.workflow_station_logins (
              id SERIAL PRIMARY KEY,
              workflow_work_order_id INTEGER REFERENCES workflow_work_orders(id) ON DELETE CASCADE,
              workflow_routing_step_id INTEGER NOT NULL REFERENCES workflow_routing_steps(id) ON DELETE CASCADE,
              station_login_id VARCHAR(160) NOT NULL,
              station_login_password VARCHAR(220) NOT NULL,
              created_at TIMESTAMP NOT NULL DEFAULT NOW(),
              updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
              CONSTRAINT uq_workflow_station_login_id UNIQUE (workflow_routing_step_id, station_login_id)
            )
            """);

        await ExecuteAsync(connection, "ALTER TABLE public.workflow_station_logins ADD COLUMN IF NOT EXISTS workflow_work_order_id INTEGER REFERENCES workflow_work_orders(id) ON DELETE CASCADE");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_workflow_station_logins_step ON public.workflow_station_logins (workflow_routing_step_id)");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_workflow_station_logins_wo ON public.workflow_station_logins (workflow_work_order_id)");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_workflow_station_logins_login ON public.workflow_station_logins (UPPER(station_login_id))");
    }

    private static async Task EnsureRoutingStepLoginColumnsAsync(NpgsqlConnection connection)
    {
        await ExecuteAsync(connection, "ALTER TABLE public.item_routing_steps ADD COLUMN IF NOT EXISTS station_login_id VARCHAR(160)");
        await ExecuteAsync(connection, "ALTER TABLE public.item_routing_steps ADD COLUMN IF NOT EXISTS station_login_password VARCHAR(220)");
        await ExecuteAsync(connection, "ALTER TABLE public.item_routing_steps ADD COLUMN IF NOT EXISTS station_ip VARCHAR(80)");
        await ExecuteAsync(connection, "ALTER TABLE public.item_routing_steps ADD COLUMN IF NOT EXISTS printer_ip VARCHAR(80)");
    }

    private static async Task EnsureSerialTrackingSchemaAsync(NpgsqlConnection connection)
    {
        await ExecuteAsync(connection, "CREATE SEQUENCE IF NOT EXISTS serial_rsn_seq START WITH 1");
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS serial_numbers (
              id BIGSERIAL PRIMARY KEY,
              sn VARCHAR(220) NOT NULL,
              rsn VARCHAR(40) NOT NULL UNIQUE DEFAULT ('RSN' || LPAD(nextval('serial_rsn_seq')::text, 10, '0')),
              work_order_id INTEGER NOT NULL REFERENCES work_orders(id) ON DELETE CASCADE,
              item_id INTEGER NOT NULL REFERENCES items(id) ON DELETE CASCADE,
              item_revision_id INTEGER REFERENCES item_revisions(id) ON DELETE SET NULL,
              site_id INTEGER REFERENCES sites(id) ON DELETE SET NULL,
              status VARCHAR(30) NOT NULL DEFAULT 'New',
              condition VARCHAR(30) NOT NULL DEFAULT 'Good',
              current_station_code VARCHAR(80),
              current_station_order INTEGER,
              last_moved_at TIMESTAMP,
              created_at TIMESTAMP NOT NULL DEFAULT NOW(),
              updated_at TIMESTAMP NOT NULL DEFAULT NOW()
            )
            """);
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_serial_numbers_sn_upper ON serial_numbers (UPPER(sn))");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_serial_numbers_wo ON serial_numbers (work_order_id)");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_serial_numbers_item ON serial_numbers (item_id)");
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS serial_station_logs (
              id BIGSERIAL PRIMARY KEY,
              serial_id BIGINT NOT NULL REFERENCES serial_numbers(id) ON DELETE CASCADE,
              item_id INTEGER NOT NULL REFERENCES items(id) ON DELETE CASCADE,
              work_order_id INTEGER NOT NULL REFERENCES work_orders(id) ON DELETE CASCADE,
              station_code VARCHAR(80) NOT NULL,
              station_name VARCHAR(220),
              action_result VARCHAR(10) NOT NULL,
              remark TEXT,
              changed_by VARCHAR(100) NOT NULL DEFAULT 'system',
              before_station_code VARCHAR(80),
              before_station_order INTEGER,
              after_station_code VARCHAR(80),
              after_station_order INTEGER,
              created_at TIMESTAMP NOT NULL DEFAULT NOW()
            )
            """);
        await ExecuteAsync(
            connection,
            """
            ALTER TABLE serial_station_logs
            ADD COLUMN IF NOT EXISTS station_length VARCHAR(40),
            ADD COLUMN IF NOT EXISTS pc_name VARCHAR(160),
            ADD COLUMN IF NOT EXISTS additional_info TEXT
            """);
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS serial_assembly_links (
              id BIGSERIAL PRIMARY KEY,
              parent_serial_id BIGINT NOT NULL REFERENCES serial_numbers(id) ON DELETE CASCADE,
              child_serial_id BIGINT NOT NULL REFERENCES serial_numbers(id) ON DELETE RESTRICT,
              station_code VARCHAR(80) NOT NULL,
              created_by VARCHAR(100) NOT NULL DEFAULT 'system',
              created_at TIMESTAMP NOT NULL DEFAULT NOW(),
              CONSTRAINT uq_serial_assembly_child UNIQUE (child_serial_id),
              CONSTRAINT uq_serial_assembly_parent_child_station UNIQUE (parent_serial_id, child_serial_id, station_code)
            )
            """);
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS packing_packages (
              id BIGSERIAL PRIMARY KEY,
              package_no VARCHAR(60) NOT NULL,
              package_type VARCHAR(20) NOT NULL CHECK (package_type IN ('BOX', 'SHIPMENT')),
              status VARCHAR(20) NOT NULL DEFAULT 'OPEN' CHECK (status IN ('OPEN', 'CLOSED', 'SHIPPED')),
              remark TEXT,
              created_by VARCHAR(100) NOT NULL DEFAULT 'system',
              created_at TIMESTAMP NOT NULL DEFAULT NOW(),
              updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
              closed_by VARCHAR(100),
              closed_at TIMESTAMP,
              shipped_by VARCHAR(100),
              shipped_at TIMESTAMP,
              CONSTRAINT uq_packing_package_no UNIQUE (package_no)
            )
            """);
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS packing_package_items (
              id BIGSERIAL PRIMARY KEY,
              package_id BIGINT NOT NULL REFERENCES packing_packages(id) ON DELETE CASCADE,
              serial_id BIGINT NOT NULL REFERENCES serial_numbers(id) ON DELETE RESTRICT,
              added_by VARCHAR(100) NOT NULL DEFAULT 'system',
              added_at TIMESTAMP NOT NULL DEFAULT NOW(),
              CONSTRAINT uq_packing_pkg_serial UNIQUE (package_id, serial_id),
              CONSTRAINT uq_packing_serial UNIQUE (serial_id)
            )
            """);
    }

    private static string GetConnectionString()
    {
        var configured = Environment.GetEnvironmentVariable("PGCONNECTIONSTRING");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var host = Environment.GetEnvironmentVariable("PGHOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("PGUSER") ?? "postgres";
        var database = Environment.GetEnvironmentVariable("PGDATABASE") ?? "MESDB";
        var password = Environment.GetEnvironmentVariable("PGPASSWORD") ?? "";
        var port = Environment.GetEnvironmentVariable("PGPORT") ?? "5432";

        return $"Host={host};Username={user};Password={password};Database={database};Port={port};Include Error Detail=true";
    }

    private static async Task<JsonNode?> ReadJsonBodyAsync(HttpRequest request)
    {
        if (request.ContentLength is null or <= 0)
        {
            return null;
        }

        using var reader = new StreamReader(request.Body, leaveOpen: true);
        var content = await reader.ReadToEndAsync();
        return string.IsNullOrWhiteSpace(content) ? null : JsonNode.Parse(content);
    }

    private static async Task<List<Dictionary<string, object?>>> QueryRowsAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            rows.Add(ReadRow(reader));
        }

        return rows;
    }

    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        AddParameters(command, parameters);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<T?> ScalarAsync<T>(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        AddParameters(command, parameters);
        var value = await command.ExecuteScalarAsync();
        if (value is null or DBNull)
        {
            return default;
        }

        return (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    private static void AddParameters(NpgsqlCommand command, params (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    private static Dictionary<string, object?> ReadRow(NpgsqlDataReader reader)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var index = 0; index < reader.FieldCount; index++)
        {
            var name = reader.GetName(index);
            if (reader.IsDBNull(index))
            {
                row[name] = null;
                continue;
            }

            var value = reader.GetValue(index);
            row[name] = value switch
            {
                DateTime dateTime => dateTime,
                string[] array => array,
                Array array => array.Cast<object?>().ToArray(),
                _ => value
            };
        }

        return row;
    }

    private static IResult JsonError(string error, int statusCode)
    {
        return Results.Json(new { error }, statusCode: statusCode);
    }

    private static IResult JsonMessage(string message, int statusCode)
    {
        return Results.Json(new { message }, statusCode: statusCode);
    }

                                                                            private static string? ReadString(JsonNode? node, string key)
    {
        return node?[key]?.GetValue<string>();
    }

    private static int? ReadInt(JsonNode? node, string key)
    {
        var value = node?[key];
        if (value is null)
        {
            return null;
        }

        return value.GetValue<int>();
    }

    private static bool? ReadBool(JsonNode? node, string key)
    {
        var value = node?[key];
        if (value is null)
        {
            return null;
        }

        return value.GetValue<bool>();
    }

        private static int ParsePositiveInt(object? value, int fallback)
    {
        return int.TryParse(value?.ToString(), out var parsed) && parsed > 0 ? parsed : fallback;
    }

    private static object? ToDbNullable<T>(T? value)
    {
        return value is null ? DBNull.Value : value;
    }
}
