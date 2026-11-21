# Pasto — PostgreSQL 整合測試與 Migration

## S7-4 HTTP 處理名額與過載重試（2026-11-21）

- `dotnet build Pasto.slnx -c Release --no-restore` 零警告、零錯誤。`dotnet format Pasto.slnx --verify-no-changes --no-restore` 與 `git diff --check` 通過。首輪格式檢查指出新增測試的初始化器須換行，修正後通過。
- 使用本機 PostgreSQL 與各測試的隔離 schema。後端回歸執行 `dotnet test Pasto.slnx -c Release --no-build --no-restore`，依下表附加 `--filter`（全套省略）及 `--logger "trx;LogFileName=<檔名>"`。TRX 位於 `tests/Pasto.IntegrationTests/TestResults/`。

  | 測試 | Filter | 通過／失敗／略過 | TRX |
  | --- | --- | --- | --- |
  | S7-4 專項與 30 人回歸 | `FullyQualifiedName~HttpWorkAdmissionTests\|FullyQualifiedName~PublicJoinAdmissionTests\|FullyQualifiedName~RequestBodyLimitTests` | 34／0／0 | `s7-4-focused.trx` |
  | S7-1、S7-2、S7-3 | `FullyQualifiedName~CredentialAdmissionTests\|FullyQualifiedName~ItemSnapshotApiTests.Snapshot_SharesReadQuota\|FullyQualifiedName~PublicJoinAdmissionTests\|FullyQualifiedName~HubAdmissionTests` | 43／0／0 | `s7-4-prior-admission.trx` |
  | 受保護 REST、SignalR、日誌、遙測 | `FullyQualifiedName~RoomStateApiTests\|FullyQualifiedName~ParticipantApiTests\|FullyQualifiedName~ItemWriteApiTests\|FullyQualifiedName~RoomClosureTests\|FullyQualifiedName~RoomHubTests\|FullyQualifiedName~TelemetryTests\|FullyQualifiedName~ServerLoggingTests` | 144／0／0 | `s7-4-rest-hub-regression.trx` |
  | 完整 PostgreSQL 回歸 | 全套 | 374／0／0 | `s7-4-full.trx` |

- 真 Kestrel 測試把一筆 Join 的 JSON 保持在未上傳完的狀態，並讓另一筆 Join 成功。另用小接收緩衝的 TCP client 停止讀取一份 300 筆 × 8 KiB 控制字元的快照，期間另一筆同類 metadata 讀取仍成功。這兩個測試使用分類名額 1，驗證上傳綁定及回應傳輸不會占住處理名額。
- 真 Kestrel 的 WebSocket 驗證使用正式 8 KiB/s、寬限 15 秒設定。協商並訂閱後，連線 32 秒只有 SignalR keepalive，接著成功收到實際新增文字的 `RoomChanged`。
- PostgreSQL 同 NAT 30 人驗收使用正式預設配額，在第一批 8 筆到達 DB 前固定佔住名額。首輪其餘 22 筆全部回 `503 Overloaded`、`Retry-After: 1`，且資料庫當時只有房主。其他來源仍能加入另一房。解除阻擋後，依相同的退避規則重試，30 個新身分均成功。另以 Edge 直接執行前端 API，使用模擬的 8 名額 HTTP gate 證明 30 次加入全可在三輪重試內成功。
- 在 `src/Pasto.Client` 執行 `npm.cmd run lint`、`npm.cmd run build` 及 `npm.cmd run test:ui -- --workers=1 --reporter=line`，均通過。完整 Edge 44 例含 6 個新增案例，驗證退避、三次上限、離頁取消、草稿保留及 `ServiceUnavailable` 不自動重送。

## S7-3 P2 與 P3 審查修正（2026-11-21）

- 修正前執行 `dotnet test Pasto.slnx -c Release --no-restore --filter "FullyQualifiedName~WebSocketWithoutNegotiatedIdIsRejectedWithoutOccupyingCapacity" --logger "trx;LogFileName=s7-3-review-p2-repro.trx"`。真 PostgreSQL 案例重現 1 例失敗，缺少 `id` 的 WebSocket 仍成功升級。
- 修正後 `dotnet build Pasto.slnx -c Release --no-restore` 零警告、零錯誤。`dotnet format Pasto.slnx --verify-no-changes --no-restore` 與 `git diff --check` 通過。
- 回歸執行 `dotnet test Pasto.slnx -c Release --no-build --no-restore`，依表附加 `--filter`（全套省略）及 `--logger "trx;LogFileName=<檔名>"`。TRX 位於 `tests/Pasto.IntegrationTests/TestResults/`。

  | 測試 | Filter | 通過／失敗／略過 | TRX |
  | --- | --- | --- | --- |
  | S7-3 專項 | `FullyQualifiedName~HubAdmissionTests` | 14／0／0 | `s7-3-review-focused.trx` |
  | S7-1、S7-2 | `FullyQualifiedName~CredentialAdmissionTests\|FullyQualifiedName~ItemSnapshotApiTests.Snapshot_SharesReadQuota\|FullyQualifiedName~PublicJoinAdmissionTests` | 29／0／0 | `s7-3-review-prior-admission.trx` |
  | 受保護 REST | `FullyQualifiedName~RoomStateApiTests\|FullyQualifiedName~ParticipantApiTests\|FullyQualifiedName~ItemWriteApiTests\|FullyQualifiedName~RoomClosureTests` | 122／0／0 | `s7-3-review-rest-regression.trx` |
  | SignalR、日誌、遙測 | `FullyQualifiedName~RoomHubTests\|FullyQualifiedName~TelemetryTests\|FullyQualifiedName~ServerLoggingTests` | 22／0／0 | `s7-3-review-hub-telemetry-regression.trx` |
  | 完整 PostgreSQL 回歸 | 全套 | 361／0／0 | `s7-3-review-full.trx` |

- 驗收檢查缺少、空值與重複 `id` 均回 400，Hub 未啟動，唯一名額仍可由正常協商的連線取得。跨來源 Origin 搭配有效的協商 token 回 403，同一 token 隨後可以用於同源連線。S7-1 的 WebSocket 驗收以真正的協商 token 驗證既有 DB 底線及失敗計費。
- 修正輪只修改後端、後端測試與文件，未重新執行前端 Lint、Build 或 Edge，沿用上一輪 Edge 38 例通過的結果。
- 獨立複核確認 middleware 順序、有效協商 token 的測試路徑與前兩批回歸證據，無提交阻擋項。

## S7-3 SignalR 連線與訂閱配額（2026-11-21）

- `dotnet build Pasto.slnx -c Release --no-restore` 零警告、零錯誤。`dotnet format Pasto.slnx --verify-no-changes --no-restore` 與 `git diff --check` 通過。
- 測試使用本機 PostgreSQL 與隔離 schema。執行 `dotnet test Pasto.slnx -c Release --no-build --no-restore`，依表附加 `--filter`（全套省略）及 `--logger "trx;LogFileName=<檔名>"`。TRX 位於 `tests/Pasto.IntegrationTests/TestResults/`。

  | 測試 | Filter | 通過／失敗／略過 | TRX |
  | --- | --- | --- | --- |
  | S7-3 專項 | `FullyQualifiedName~HubAdmissionTests` | 10／0／0 | `s7-3-focused.trx` |
  | S7-1、S7-2 | `FullyQualifiedName~CredentialAdmissionTests\|FullyQualifiedName~ItemSnapshotApiTests.Snapshot_SharesReadQuota\|FullyQualifiedName~PublicJoinAdmissionTests` | 29／0／0 | `s7-3-prior-admission.trx` |
  | 受保護 REST | `FullyQualifiedName~RoomStateApiTests\|FullyQualifiedName~ParticipantApiTests\|FullyQualifiedName~ItemWriteApiTests\|FullyQualifiedName~RoomClosureTests` | 122／0／0 | `s7-3-rest-regression.trx` |
  | SignalR、日誌、遙測 | `FullyQualifiedName~RoomHubTests\|FullyQualifiedName~TelemetryTests\|FullyQualifiedName~ServerLoggingTests` | 22／0／0 | `s7-3-hub-telemetry-regression.trx` |
  | 完整 PostgreSQL 回歸 | 全套 | 357／0／0 | `s7-3-full.trx` |

- 後端專項驗證四層同時爭搶與冪等釋放、已驗證協商速率與滿載回收、缺少或格式錯誤的憑證不占已驗證額度、SSE／Long Polling 拒絕，以及訂閱額度在 DB 查詢前檢查。真實 SignalR 連線中，兩筆 negotiate 都通過後，先取得名額的連線成功訂閱，輸方收到 type 7、`HubException: HubConnectionLimitReached`，且 `allowReconnect` 未啟用。取得名額後注入初始化例外，測試觀察到 transport 清理、Hub `OnDisconnectedAsync` 未執行，下一條連線仍能取得名額。
- 在 `src/Pasto.Client` 執行 `npm.cmd run lint`、`npm.cmd run build`、`npm.cmd run test:ui -- --workers=1 --reporter=line`，均通過。Edge 共 38 例，包含 6 個 S7-3 專項。使用模擬的 negotiate 429、協定 Close 與 WebSocket 建立失敗，驗證長退避、REST 持續更新和訂閱回復；另以實際 SignalR JS 套件核對 Close 不會進入內建自動重連。首輪前端 Build 因測試中 SignalR 全域型別轉型而失敗，改為檢查可選欄位後通過。

## S7-2 P1 與 P3 審查修正（2026-11-21）

- 修正前執行 `dotnet test Pasto.slnx -c Release --no-restore --filter "FullyQualifiedName~FirstNewLookupMissAfterCooldownImmediatelyReturns120SecondRetry" --logger "trx;LogFileName=s7-2-review-p1-repro.trx"`。真 PostgreSQL 案例重現 1 例失敗，60 秒冷卻後第一個新錯碼預期 429、實際回 404。
- 修正後 `dotnet build Pasto.slnx -c Release --no-restore` 零警告、零錯誤。`dotnet format Pasto.slnx --verify-no-changes --no-restore` 與 `git diff --check` 通過。
- 回歸執行 `dotnet test Pasto.slnx -c Release --no-build --no-restore`，依下表附加 `--filter`（全套省略）與 `--logger "trx;LogFileName=<檔名>"`。TRX 位於 `tests/Pasto.IntegrationTests/TestResults/`。

  | 測試 | Filter | 結果（通過／失敗／略過） | TRX |
  | --- | --- | --- | --- |
  | S7-2 專項 | `FullyQualifiedName~PublicJoinAdmissionTests` | 14／0／0 | `s7-2-review-focused.trx` |
  | S7 第一批專項 | `FullyQualifiedName~CredentialAdmissionTests\|FullyQualifiedName~ItemSnapshotApiTests.Snapshot_SharesReadQuota` | 15／0／0 | `s7-2-review-s7-1-regression.trx` |
  | 受保護 REST | `FullyQualifiedName~RoomStateApiTests\|FullyQualifiedName~ParticipantApiTests\|FullyQualifiedName~ItemWriteApiTests\|FullyQualifiedName~RoomClosureTests` | 122／0／0 | `s7-2-review-rest-regression.trx` |
  | SignalR、日誌、遙測 | `FullyQualifiedName~RoomHubTests\|FullyQualifiedName~TelemetryTests\|FullyQualifiedName~ServerLoggingTests` | 22／0／0 | `s7-2-review-hub-telemetry-regression.trx` |
  | 完整 PostgreSQL 回歸 | 全套 | 347／0／0 | `s7-2-review-full.trx` |

- 專項驗證 20 筆同時期錯碼後的重犯立即回 429／120 秒、保留指紋去重及 300 秒上限。到第 720 秒時最初的 20 筆已過 10 分鐘，測試補滿視窗再確認下次仍為 300 秒。滿載案例驗證表外來源仍可 Join，超過每來源 Join 桶後回 429，並核對 EventId 6103 的節流筆數。預設值案例以同一來源併發建立 30 個不同的 Participant，第 31 筆收到 429 與 `Retry-After`。
- 這輪修改後端、後端測試與文件，前端 Lint、Build 與 Edge 未重跑。Edge 沿用第一批的 32 例通過結果。
- 獨立複核確認四項修正與專項 TRX。F1 以最舊指紋汰換保留 20 筆上限，被汰換的房碼在原視窗內可能再次計入。完整 PostgreSQL 回歸由實作方執行。

## S7-2 公開 Join 防護（2026-11-21）

- `dotnet build Pasto.slnx -c Release --no-restore` 通過，零警告、零錯誤。`dotnet format Pasto.slnx --verify-no-changes --no-restore` 與 `git diff --check` 通過。
- 使用本機 PostgreSQL 18.6 的 `pasto_test`，每例依既有 fixture 建立隔離 schema。執行 `dotnet test Pasto.slnx -c Release --no-build --no-restore`，依下表附加 `--filter`（全套省略）與 `--logger "trx;LogFileName=<檔名>"`。TRX 位於 `tests/Pasto.IntegrationTests/TestResults/`。

  | 測試 | Filter | 結果（通過／失敗／略過） | TRX |
  | --- | --- | --- | --- |
  | S7-2 專項 | `FullyQualifiedName~PublicJoinAdmissionTests` | 12／0／0 | `s7-2-focused.trx` |
  | S7 第一批專項 | `FullyQualifiedName~CredentialAdmissionTests\|FullyQualifiedName~ItemSnapshotApiTests.Snapshot_SharesReadQuota` | 15／0／0 | `s7-2-s7-1-regression.trx` |
  | 受保護 REST | `FullyQualifiedName~RoomStateApiTests\|FullyQualifiedName~ParticipantApiTests\|FullyQualifiedName~ItemWriteApiTests\|FullyQualifiedName~RoomClosureTests` | 122／0／0 | `s7-2-rest-regression.trx` |
  | SignalR、日誌、遙測 | `FullyQualifiedName~RoomHubTests\|FullyQualifiedName~TelemetryTests\|FullyQualifiedName~ServerLoggingTests` | 22／0／0 | `s7-2-hub-telemetry-regression.trx` |
  | 完整 PostgreSQL 回歸 | 全套 | 345／0／0 | `s7-2-full.trx` |

- 專項涵蓋同 NAT 的 30 次相同失效碼、20 筆不同錯碼門檻、IPv6 /64、滾動視窗與退避、冷卻表滿載和每分鐘日誌節流、全站 300 筆計數與兩段恢復、壓力期間已驗證的 REST 和 WebSocket Hub。每房測試以兩個來源送出 30 筆併發 Join，核對獨立 Participant ID、第 31 筆 429、其他房間不受影響及每 2 秒回補一格。另用 `pg_blocking_pids` 證明兩個交易競爭最後一格，並驗證房間已滿、失效、憑證恢復、寫入失敗及提交失敗的計費差異。
- 初次同 NAT 去重驗收調高既有來源 Join 桶，以單獨驗證冷卻。當時正式預設每來源 burst 仍為 20，後續審查修正將預設調為 30。
- 首輪 `dotnet test Pasto.slnx -c Release --no-restore --filter "FullyQualifiedName~PublicJoinAdmissionTests" --logger "trx;LogFileName=s7-2-focused-initial.trx"` 為 10 通過、1 失敗。滿載 Warning 已出現在 Console，測試日誌接收端對 Serilog 轉送的結構欄位數假設錯誤。修正測試收集後，改在測試本體驗證 EventId、層級、彙總訊息與節流。
- 獨立複核指出冷卻開始時清空集合會讓同一房碼在原 10 分鐘視窗內再次計費。修正後保留各筆首次計時，並驗證冷卻結束後的去重與再犯退避。審查方複核修正並核對專項 TRX；完整 PostgreSQL 回歸由實作方執行。
- S7-2 只修改後端、後端測試與文件。前端 Lint、Build、Edge 未執行。

## S7 第一批憑證失敗計費（2026-11-21）

- `dotnet build Pasto.slnx -c Release --no-restore` 零警告、零錯誤，`dotnet format Pasto.slnx --verify-no-changes --no-restore` 與 `git diff --check` 通過。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore --logger "trx;LogFileName=s7-credential-admission-full-verified.trx"` 完整 PostgreSQL 回歸 332 通過、0 失敗、0 略過，耗時 1 分 22 秒。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore --filter "FullyQualifiedName~CredentialAdmissionTests|FullyQualifiedName~ItemSnapshotApiTests.Snapshot_SharesReadQuota" --logger "trx;LogFileName=s7-credential-admission-final-focused.trx"` 共 14 例通過。涵蓋可信代理前綴分組、建房／加入、REST／negotiate／WebSocket 入口、跨房計費、併發 DB 查詢上限、快取 TTL／容量／淘汰、指標與節流日誌。同 NAT 測試有 4 筆冷啟動查詢同時回傳查無，接續 60 筆直接回 401，同一舊憑證只扣一格，新有效憑證仍可讀寫。
- 受保護 REST 回歸 122 例、SignalR／日誌／遙測回歸 22 例，均無失敗或略過。使用 `dotnet test Pasto.slnx -c Release --no-build --no-restore`，分別加下列 filter 與 `--logger "trx;LogFileName=<檔名>"`。TRX 保留在 `tests/Pasto.IntegrationTests/TestResults/`。

  | Filter | TRX |
  | --- | --- |
  | `FullyQualifiedName~RoomStateApiTests\|FullyQualifiedName~ParticipantApiTests\|FullyQualifiedName~ItemWriteApiTests\|FullyQualifiedName~RoomClosureTests` | `s7-rest-regression.trx` |
  | `FullyQualifiedName~RoomHubTests\|FullyQualifiedName~TelemetryTests\|FullyQualifiedName~ServerLoggingTests` | `s7-hub-telemetry-regression.trx` |

- `npm.cmd run lint`、`npm.cmd run build`、`npm.cmd run test:ui -- --workers=1 --reporter=line` 均通過。Edge 共 32 例，其中 3 例驗證 429 時手動刷新與前景事件不跳過退避、REST 仍可讀寫，以及首次協商和自動重連遇 401 均立即進入身分失效流程。Windows 本機的 Playwright worker 結束後暫時保留 Vite 子程序，結束該次測試所啟動的 Vite 後，Playwright 回傳 32 passed 與 exit 0。
- 首輪專項 `s7-credential-admission-focused.trx` 有 8 例失敗，原因為測試 SQL 計數綁定了查詢別名，以及 TestServer 傳 JSON 時未建立正常的 body feature。改用欄位條件識別查詢並透過 TestServer 的 HTTP handler 傳送 JSON 後，`s7-credential-admission-verified.trx` 的 10 例全過。首輪格式檢查另發現測試的物件初始化器需要換行，修正後通過。
- 首輪完整回歸 `s7-credential-admission-full.trx` 為 329 通過、2 失敗。兩個快照配額案例仍以缺少憑證的請求消耗 DB 底線，預期 429，實際依新規格回 401。修正案例用不同的格式正確憑證驗證 DB 底線，另由新專項案例斷言缺少與格式錯誤的憑證不計費也不查 DB。

### S7 第一批 P1 審查修正（2026-11-21）

- 修正前執行 `dotnet test Pasto.slnx -c Release --no-restore --filter "FullyQualifiedName~FullFailureSourceTableLeavesNewSourcesSubjectToDatabaseFloorOnly|FullyQualifiedName~FailureCacheAndSourceCapacityAreBoundedAndUseMonotonicExpiry" --logger "trx;LogFileName=s7-credential-admission-review-repro.trx"`。兩例皆重現失敗。新來源第二次查無預期 401、實際為 429，直接預檢預期可通過、實際回 Retry-After 1 秒。
- 修正後執行 `dotnet build Pasto.slnx -c Release --no-restore`，零警告、零錯誤。`dotnet format Pasto.slnx --verify-no-changes --no-restore` 與 `git diff --check` 通過。
- 回歸執行 `dotnet test Pasto.slnx -c Release --no-build --no-restore`，依下表加 `--filter` 及 `--logger "trx;LogFileName=<檔名>"`。

  | 測試 | Filter | TRX |
  | --- | --- | --- |
  | 專項 15 例通過 | `FullyQualifiedName~CredentialAdmissionTests\|FullyQualifiedName~ItemSnapshotApiTests.Snapshot_SharesReadQuota` | `s7-credential-admission-review-focused.trx` |
  | 受保護 REST 122 例通過 | `FullyQualifiedName~RoomStateApiTests\|FullyQualifiedName~ParticipantApiTests\|FullyQualifiedName~ItemWriteApiTests\|FullyQualifiedName~RoomClosureTests` | `s7-credential-admission-review-rest.trx` |
  | SignalR／日誌／遙測 22 例通過 | `FullyQualifiedName~RoomHubTests\|FullyQualifiedName~TelemetryTests\|FullyQualifiedName~ServerLoggingTests` | `s7-credential-admission-review-hub-telemetry.trx` |
  | 完整 PostgreSQL 333 例通過 | 全套 | `s7-credential-admission-review-full.trx` |

- 修正後四組均為 0 失敗、0 略過，完整 PostgreSQL 回歸耗時 1 分 23 秒。
- 用真 PostgreSQL 與 DI 注入兩筆來源上限。表滿時，不在表中的來源可以查無並回 401，重送同憑證命中快取。新來源第 7 筆需查詢的請求被每來源底線拒絕，全站第 17 筆需查詢的請求被全站底線拒絕，兩次拒絕均未查 DB。有效憑證的 me、快照、新增、刪除、關房及 Hub negotiate 皆成功。既有來源的失敗桶耗盡後仍在 DB 查詢前回 429。手動時鐘驗證 `6102` 在 59 秒時仍節流，滿 60 秒的下一次未計費事件才送出下一筆摘要。
- 本輪修改後端與文件。Edge 沿用第一批 32 例通過的結果，本輪未重跑。

## Logging、Health Checks 與 OpenTelemetry（2026-11-21）

- Release Build 0 警告、0 錯誤，`dotnet format Pasto.slnx --verify-no-changes --no-restore` 與 `git diff --check` 通過。Health 測試初始化器首次格式檢查未通過，修正換行後重跑通過。
- Logging 專項 63 例、Health Checks 專項 14 例通過。驗證正式 Console JSON 的固定 EventId／W3C 關聯與單次輸出、測試 provider 收到安全的應用程式事件、Hub 未預期例外不輸出原始例外、DB 不可達只影響 readiness，以及 Worker 的狀態獨立回報。
- OpenTelemetry 專項 2 例通過。實際 HTTP span 匯出測試保留路由樣板，去除原始 URL、headers、trace state 等資料。以 MeterListener 驗證文字新增與清理失敗計數沒有 tags。最終 `dotnet test Pasto.slnx -c Release --no-build --no-restore` 完整 PostgreSQL 回歸 313 通過、0 失敗、0 略過，耗時 1 分 21 秒。TRX 為 `observability-full.trx`。
- 以獨立 `mcr.microsoft.com/aspire/dashboard:13.6.5`、loopback 連接埠、前端／OTLP／查詢驗證及記憶體儲存進行本機 smoke test。以 Dashboard 查詢 API 確認 `Pasto.Server` 有接收 metrics 與 HTTP span，協商 span 保留 `/hubs/room/negotiate`，輸出無測試用房碼、`access_token` 或 URL attributes。此階段收 HTTP negotiate 與握手的 span，Hub 方法 ActivitySource 尚未納入。
- Serilog 仍輸出 Console。驗證時以暫存的 OTLP Protobuf 測試轉送程式，將實際的安全 `6003` Console 事件送入 Dashboard 並讀回，確認測試用敏感文字未出現。首次手動 JSON 轉送未收到確定回應，改用 Protobuf 確認。本次未加入產品日誌 exporter。
- 暫存版 `dotnet-counters` 10.0.750501 對執行中的 Kestrel 讀得 `http.server.request.duration` 及 `pasto.cleanup.failures`。首次取樣只捕捉自訂指標，於訂閱開始後補發實際請求，第二次取樣成功取得內建 HTTP 指標。專案工具清單與部署 Compose 維持原樣。

### S8 自訂失敗計數語意驗證（2026-11-21）

- `dotnet build Pasto.slnx -c Release --no-restore` 零警告、零錯誤，`dotnet format Pasto.slnx --verify-no-changes --no-restore` 與 `git diff --check` 通過。
- PostgreSQL 專項回歸 101 通過、0 失敗、0 略過。涵蓋 Telemetry、Room API、Item 寫入、清理與健康探針。新增案例確認 Item 的 400／401／404／409 不計入，儲存失敗與房間忙碌的 503、未預期的 500 各計 1。以中斷房碼回收查詢驗證 `6008` 仍會記錄，單獨發生時不增加計數，與主資料失敗同輪時只增加 1。兩個計數均無 tag。TRX 位於 `tests/Pasto.IntegrationTests/TestResults/metric-semantics-focused.trx`。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore` 完整 PostgreSQL 回歸 321 通過、0 失敗、0 略過，耗時 1 分 23 秒。TRX 位於 `tests/Pasto.IntegrationTests/TestResults/metric-semantics-full.trx`。
- 本次驗證只涉及計數口徑，Aspire Dashboard 與 `dotnet-counters` 的 smoke test 沿用前一輪紀錄。

## S6 訂閱追蹤滿載 P2（2026-11-21）

- Release Build 與格式檢查第一次因測試寫法觸發 xUnit2031，改用 `Assert.Single` 的 predicate overload，維持斷言。重跑 `dotnet build Pasto.slnx -c Release --no-restore` 零警告、零錯誤，`dotnet format Pasto.slnx --verify-no-changes --no-restore` 與 `git diff --check` 通過。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore --filter "FullyQualifiedName~RoomCodeCleanupTests|FullyQualifiedName~RoomHubTests|FullyQualifiedName~RoomCleanupTests"` 在 Docker Compose PostgreSQL 上 27 例通過，0 失敗、0 略過。容量設為 1 時，兩條未追蹤連線仍成功訂閱並收到更新及關房提示，重新訂閱已關房時收到 `RoomUnavailable`。已有未追蹤連線不補登，釋出名額後新連線可登記。Worker 只記一筆 `6009`，`UntrackedSubscriptions` 為 2，累計值取走後無重複摘要。並行登記、已取消連線、異房登記與移除不存在紀錄亦有斷言。原有 25 例測試與斷言保留。TRX 位於 `tests/Pasto.IntegrationTests/TestResults/s6-second-p2-focused.trx`。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore` 完整 PostgreSQL 回歸 307 通過、0 失敗、0 略過，耗時 1 分 20 秒。TRX 位於 `tests/Pasto.IntegrationTests/TestResults/s6-second-p2-full.trx`。

## S6 房碼回收與 SignalR 連線清理（2026-11-20）

- `dotnet build Pasto.slnx -c Release --no-restore` 通過，零警告、零錯誤。`dotnet format Pasto.slnx --verify-no-changes --no-restore` 與 `git diff --check` 通過。
- 真 PostgreSQL 驗證提前關房保留原冷卻期限、期限已到但仍有 Room 引用時保留、跳鎖後取滿 100 筆、啟動跨批補清及重跑。並行測試透過 `pg_blocking_pids` 證明建房等待回收交易。回收提交後可重配相同碼，舊 Credential 對新房回 401，新 Credential 回 200；回收回滾則舊碼仍被保留。
- SignalR 使用測試 Host 的 WebSocket 與真 PostgreSQL，驗證提前關房、重複訂閱、關房與第二次訂閱檢查的競爭、通知故障、Worker 補清到期／已關／已刪房、他房仍可接收提示，以及 Worker 停用時的已關房 REST 和新訂閱拒絕。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore --filter "FullyQualifiedName~RoomCodeCleanupTests|FullyQualifiedName~RoomHubTests|FullyQualifiedName~RoomCleanupTests"` 通過 25 例、0 失敗、0 略過。TRX 位於 `tests/Pasto.IntegrationTests/TestResults/s6-second-focused.trx`。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore` 完整回歸 305 通過、0 失敗、0 略過，耗時 1 分 21 秒。TRX 位於 `tests/Pasto.IntegrationTests/TestResults/s6-second-full.trx`。1,500 條追蹤的高負載行為及重房積壓清理時限仍待壓測。

## S6 整輪預算 P3 驗證（2026-11-20）

- `dotnet build Pasto.slnx -c Release --no-restore` 通過，零警告、零錯誤。`dotnet format Pasto.slnx --verify-no-changes --no-restore` 與 `git diff --check` 通過。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore --filter FullyQualifiedName~RoomCleanupTests` 通過 12 例，0 失敗、0 略過。原有 10 例斷言維持原樣。新增 2 例在測試 Host 使用 scripted store 與手動時鐘，直接推進 20 秒期限，單例實際用時約 2.2 及 2.9 秒。驗證進度事件 `6006` 為 Warning、欄位為 2 批／200 房，無進度以 `6002` 累加失敗，以及有先前的失敗時仍能歸零。TRX 位於 `tests/Pasto.IntegrationTests/TestResults/s6-p3-focused.trx`。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore` 在 Docker Compose PostgreSQL 執行完整回歸，296 通過、0 失敗、0 略過，耗時 1 分 20 秒。TRX 位於 `tests/Pasto.IntegrationTests/TestResults/s6-p3-full.trx`。

## S6 主資料清理收尾驗證（2026-11-20）

- `dotnet build Pasto.slnx -c Release --no-restore` 通過，零警告、零錯誤。`dotnet format Pasto.slnx --verify-no-changes --no-restore` 通過。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore --filter FullyQualifiedName~RoomCleanupTests` 在本機 Docker Compose PostgreSQL 上 10 例通過，0 失敗、0 略過。涵蓋同一 Host 的失敗輪數 1 → 2 → 成功 → 1、未預期例外的安全日誌與 API 存活、批次 100 → 50 → 60、跨輪保留、單房逾時後結束當輪、PostgreSQL 與 Npgsql 指令逾時回滾。TRX 位於 `tests/Pasto.IntegrationTests/TestResults/s6-hardening-focused.trx`。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore` 完整回歸 294 通過、0 失敗、0 略過，耗時 1 分 20 秒。TRX 位於 `tests/Pasto.IntegrationTests/TestResults/s6-hardening-full.trx`。

## S6 主資料清理驗證（2026-11-20）

- Docker Compose 的 PostgreSQL 健康檢查通過。.NET Release Build 零警告、零錯誤，`dotnet format Pasto.slnx --verify-no-changes --no-restore` 與 `git diff --check` 通過。
- S6 專項 PostgreSQL 測試 6 例通過。涵蓋 102 個交錯失效房間的跨類別排序及分批、跳過鎖定房間及解鎖後補清、重跑、提交時失敗與 PostgreSQL 敘述逾時的回滾、待清量查詢故障後繼續處理多批，以及主機啟動補清。亦驗證停用設定可由測試主機覆寫、API 與 Worker 使用不同連線池、有效房間及房碼保留，清理後舊 Credential 回 401。TRX 位於 `tests/Pasto.IntegrationTests/TestResults/s6-main-data.trx`。
- 首輪完整測試 289 通過、1 失敗。既有 `FullRoom_ReturnsAllMaxSizeItemsAndHasMatchingOrderIndex` 的內容和順序斷言通過，EXPLAIN 卻選用 `ix_items_owner` 加 `Sort`，觸發索引名稱斷言。S6 版本單跑該案例通過，未修改測試的基準提交 `85bc2db` 完整 284 例通過。在基準提交的暫存副本對測試資料執行 `ANALYZE clipboard_items` 後，重現同一個索引選擇和斷言失敗。此診斷證明既有斷言會受統計影響，未確認首輪是哪次統計更新造成。
- 最終完整 Release 測試 290 通過、0 失敗、0 略過，耗時 1 分 21 秒。TRX 位於 `tests/Pasto.IntegrationTests/TestResults/s6-release.trx`。
- 另以暫存探針執行正式 Worker 與 Application 流程，令第一批回報滿批、第二批執行 PostgreSQL `pg_sleep(29)`。第二批於啟動後 20.02 秒收到取消，未開始第三批。此探針驗證取消傳到資料庫，正式刪除交易的 5 秒敘述逾時回滾由 S6 專項測試覆蓋。滿載下 10 分鐘內實體刪除的目標尚待量測。

## S5 合併前修整驗證（2026-11-20）

- 修正前先跑兩個新案例。跨午夜案例在日期分隔後第一則訊息的可見作者標籤斷言出現 `element(s) not found`，重現缺失。平滑捲動中斷案例通過。
- 以日期分隔線作為分組邊界後，`npm run lint`、`npm run build` 和 `npm run test:ui -- --workers=1` 均通過，Edge 共 28 例、0 失敗。新增案例驗證台北時間跨午夜首則訊息顯示作者，以及動畫途中按 Home 後在下一次快照更新時維持閱讀位置並增加未讀數。
- 2026-11-20 的合併前驗證回報如下。Claude 在修整後重跑前端檢查，本輪兩個新增案例均通過。

| 驗證 | 來源 | 結果 |
| --- | --- | --- |
| .NET Release Build、`dotnet format --verify-no-changes` | Claude，11/20 | 通過，零警告、零錯誤 |
| PostgreSQL 整合測試 | 11/20 驗證回報 | 284／284 通過 |
| `npm run lint`、`npm run build`（含 `tsc --noEmit`） | Claude，11/20 修整後 | 均通過，Lint 零警告 |
| Edge E2E | Claude，11/20 修整後 | 28 通過、0 失敗，29.6 秒。第 7 例跨午夜作者標籤及第 18 例鍵盤中斷平滑捲動均通過 |

- 11/20 的整合測試數字取自合併前驗證回報。另保留本機可核對的 `tests/Pasto.IntegrationTests/TestResults/s5-round3.trx`，其執行時間為 11/19 20:50（Asia/Taipei），284 通過、0 失敗、0 略過。
- 雙 Edge 搭配真後端的實測為 Codex 的紀錄，Claude 尚未獨立重跑。審查結論為無阻擋合併的問題。

## S5 第三輪審查與作者代碼驗證（2026-11-19）

- Release Build 零警告、零錯誤。`dotnet format Pasto.slnx --verify-no-changes --no-restore` 通過。完整 PostgreSQL 整合測試 284 passed／0 failed／0 skipped，TRX 位於 `tests/Pasto.IntegrationTests/TestResults/s5-round3.trx`。
- 後端用固定 UUID 組合經真 PostgreSQL 的 `/me` 驗證預先獨立算出的 SHA-256 向量 `0653C`。建房／加入、`/me` 與兩個身分讀取的快照驗證相同代碼，且快照的房主旗標與公開欄位符合契約。
- `npm run lint`、`npm run build` 與 Edge E2E 26 例通過。涵蓋 A／B／C 視角、360px header、兩種主題、搜尋結果分組、隱藏標籤後的可及名稱、舊 session 恢復，以及兩項 P3。首次 TypeScript Build 發生 `TS2352`，修正測試注入函式的視窗屬性型別宣告後重跑通過。
- 兩個獨立的 360px Edge 工作階段經 Vite、Kestrel、SignalR WebSocket 與隔離 PostgreSQL schema 實測房主及訪客。雙方各送一則文字，確認房主與訪客的標籤、訪客自己的代碼等於房主畫面上的訪客代碼、配色一致，以及訪客重整後可恢復原代碼。

## S5 房間介面驗證（2026-11-19）

- 完整 PostgreSQL 整合測試 282 例與 Edge 20 例通過。快照測試驗證 `authorColor` 為 0～5，同一作者的訊息跨身分讀取時顏色索引一致。Edge 新增案例驗證淺色與深色主題中的訪客標籤配色、訊息正文顏色，以及智慧捲動、重連保留閱讀位置和跳到最新後清除計數。
- Release Build、格式檢查、前端 Lint 與 Build 通過。Edge 從正式建置預覽取得兩個有版本的 Pasto favicon 資產，回應與原始檔一致，SVG 可解碼。

## S5 審查修正驗證（2026-11-19）

- `npm run lint` 與 `npm run build` 通過，Build 包含 TypeScript 檢查。完整 Edge 18 例通過，包括原有 14 例及四項回歸測試。
- 新增案例驗證 Hub 協商回 403 時仍能載入、重新整理、輪詢與送出；慢快照期間可送出；快照連續三次失敗後仍可新增與刪除；通知觸發的快照早於 POST 回應顯示時，送出者會捲到底，後續其他人的變更維持捲動位置。
- 兩個獨立的 Edge 工作階段都攔截 Hub 協商並回 403。透過 Vite、Kestrel 與隔離的 PostgreSQL schema 實測首次載入、手動更新、跨工作階段補查、新增、刪除與關房，全部通過。
- 本輪修正範圍為前端，既有後端 282 例未重跑。

## S5 SignalR 與整份快照同步驗證（2026-11-19）

- Release Build 0 warning／0 error，`dotnet format Pasto.slnx --verify-no-changes --no-restore` 通過。完整 Release 測試 282 passed／0 failed／0 skipped，TRX 位於 `tests/Pasto.IntegrationTests/TestResults/s5-release.trx`。
- `RoomHubTests` 使用真 PostgreSQL 與 SignalR，驗證同房授權、跨房隔離、提示只含房間 UUID 與十進位版本字串、關房後拒絕重訂閱。分別在 commit 前後注入中斷，兩者都未發送提示，只有後者留下資料。提交成功後注入通知故障，仍回傳成功，資料可由快照讀取。
- 前端 `npm run lint`、`npm run build` 通過，後者包含 TypeScript 檢查。Edge 14 例通過，涵蓋先訂閱再讀、重複與亂序通知合併、慢快照期間維持單一請求、舊 401／404 不清除重新加入的房間，以及重連與前景補查。
- 兩個獨立的 Edge 工作階段經 Vite、Kestrel WebSocket 與隔離 PostgreSQL schema 實測新增、刪除、重新訂閱和關房。測試工具刻意丟棄訪客端最後一次 `RoomChanged`，正常前景頁面兩次分別在 17.15 秒與 18.68 秒補回資料。壓測與正式反向代理驗證留在 S7／S8。
- 初次 API 測試出現 `No password has been provided`，修正測試程序的連線設定後通過。初次雙瀏覽器實測只關閉代理的上游 socket，等待訪客顯示斷線狀態逾時，改由測試工具關閉瀏覽器端 socket 後通過。

## S3-02 文字快照驗證（2026-11-18）

- 完整 Release 回歸 278 passed／0 failed／0 skipped（`s3-02-full.trx`）。
- Release Build 0 warning／0 error，format verify 通過。相關 `ItemSnapshotApiTests` 9 passed／0 failed／0 skipped（`s3-02-related-verified.trx`）。首輪 7 passed／2 failed（`s3-02-related.trx`）：兩個測試攔截器使用 `items` 而非實際的 `clipboard_items`，未觸發並行檢查與 EXPLAIN。修正測試的資料表比對，未改產品行為或放寬斷言。
- 滿房經正式 `AddItem` 建立 300 筆各 8192 UTF-8 bytes 的 CJK 文字，逐筆比對順序與內容。這組輸入的 HTTP JSON 回應本體實測 2,503,752 bytes，數字包含欄位與 JSON 編碼，會隨內容與時間格式而變動。
- 最壞情況估算：`AddItem` 允許 `U+0001`，審查方用獨立 .NET 10 程式確認 ASP.NET Core 預設 JSON 會將這個 1 byte 字元輸出為 6 bytes 的 `\u0001`。滿房若每筆 8192 bytes 都是這類字元，文字內容的 JSON 編碼為 `300 × 8192 × 6 = 14,745,600 bytes`，加上欄位後回應約 14.8 MB。這是推算值，尚未透過 API 實測。S7 量測這類滿房回應本體與讀取配額下的頻寬，S8 納入 1,000 條連線同時重抓的情境。
- 對實際 EF SQL 執行 EXPLAIN。單房測試資料的預設計畫使用 `ix_items_owner` 的 Bitmap Index Scan 再排序。測試以交易內設定排除 sequential scan 與 bitmap scan，確認同一查詢可透過 `ix_items_order` 依 RoomId 篩選且無需 Sort。產品未限制 PostgreSQL 選擇計畫，代表性資料的計畫與多連線負載仍待後續驗證。

## S3-02 審查方獨立 review 與實測（2026-11-18）

- 審查方（Claude）在 `a05d87f` 驗證 `df2510d..a05d87f`，工作目錄只有四份文件有未提交修改。無阻擋問題，唯一非阻擋 P3 為滿房 JSON 大小的最壞情況估算已補在上節。
- Release Build 0 warning／0 error，`dotnet format --verify-no-changes` exit 0，殘留 `pasto_it_*` schema 為 0。相關測試 12 例通過（快照 9 例、連線池 3 例），完整測試同一命令緊接兩輪各 278 passed／0 failed／0 skipped，各約 13 秒。
- 已核對三份 TRX 的計數：`s3-02-claude-review-related.trx`、`s3-02-claude-review-full-1.trx`、`s3-02-claude-review-full-2.trx`，皆位於 `tests/Pasto.IntegrationTests/TestResults/`。
- 尚未驗證：滿房控制字元的實際 HTTP 回應大小、將交易改成 READ COMMITTED 後一致性測試的結果、1,000 條連線同時重抓的負載。

## S3-01 P3 測試補強（2026-11-18）

- 改用交易外的 `SET lock_timeout`。測試確認設定值先改變，並斷言重新借用同一個 PostgreSQL backend 後恢復為原值。
- Release Build 0 warning／0 error，format verify 通過，相關 3 例（`s3-01-pool-p3-related.trx`）與完整 269 例（`s3-01-pool-p3-full.trx`）皆通過，0 failed／0 skipped。

## S3-01 審查方獨立 review 與實測（2026-11-18）

- 審查方（Claude）審查 `408593e..ad5ff82`，重點為 `ad5ff82` 測試設施修正；在 `feature/s3-01-item-write-api` 的 `da98a83`（僅文件晚於 `ad5ff82`）、乾淨工作目錄上獨立執行。
- 結論：無 P0／P1／P2，無 Blocking Issue。確認每個 fixture 自有 pool、歸還時 DISCARD ALL 後 search_path 仍為連線啟動參數、先關 pool 再以不入池連線 DROP schema、初始化失敗依 `schemaCreated` 分流清理；API host 的 NpgsqlDataSource 由 host 釋放，`await using` 逆序保證先於 fixture 釋放；直接使用 fixture 的最大並行為 8，未超過 MaxPoolSize 16。
- P3（非阻擋）：`TestDatabasePoolingTests.SequentialContexts_ReuseBackend_AndResetSessionAndTransactionSettings` 的 `SET LOCAL lock_timeout` 在交易 rollback 時即還原，即使 `NoResetOnClose=true` 也會通過，無法驗證 pool reset；session reset 實際只由 `application_name` 驗證。建議改為交易外的 session 層級 SET，或移除該段並使測試名稱符合實際驗證內容。尚未修正。
- `docker compose up -d --wait`：PostgreSQL healthy。
- `dotnet build Pasto.slnx -c Release --no-restore`：0 warning／0 error。
- 相關 `--filter FullyQualifiedName~TestDatabasePoolingTests`：3 passed／0 failed／0 skipped。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore` 同一命令緊接兩輪：各 269 passed／0 failed／0 skipped，各約 12 秒。
- 測試後 `pasto_it_*` schema 為 0；`pasto_test` 僅剩查詢用的 1 條 psql 連線。`dotnet format Pasto.slnx --verify-no-changes --no-restore`：exit 0。
- TRX：`s3-01-claude-review-related.trx`、`s3-01-claude-review-full-1.trx`／`-2.trx`，位於被忽略的 `tests/Pasto.IntegrationTests/TestResults`。
- 未執行：TIME_WAIT 取樣（4837 → 544 為實作方紀錄，未由審查方重測）、刻意重現 port 耗盡。

## S3-01 審查修正：測試連線池（2026-11-18）

PostgresTestDatabase 改為每個 fixture 擁有一個 NpgsqlDataSource，Pooling=true、MinPoolSize=0、MaxPoolSize=16、NoResetOnClose=false。context／正式 use case 共用該 fixture 的 pool；schema 仍逐測試隔離。初始化先歸還建 schema 的 lease，再做正式 Migration；Dispose 先關閉自有 data source，再用一條不入池的連線 DROP 自有 schema。不使用 ClearAllPools，也不改正式 Program 的連線設定或測試併發程度。

新增 TestDatabasePoolingTests 3 例：確認連續 context 重用 backend 並重設 session／交易設定，同時持有的 context PID 不同且 search_path 隔離，以及 dispose 後 idle backend 結束、只有自有 schema 被刪。既有鎖競爭測試的 pg_blocking_pids 斷言保持原樣。

同一台 Windows、相同沙箱外執行方式，以 netstat 每約 500 ms 取樣遠端 port 55432 的 TIME_WAIT；峰值是取樣最大值，不宣稱捕捉到所有瞬間峰值。修改後最後兩輪在同一命令中緊接執行，沒有等待 TIME_WAIT 退完。

| 執行 | 通過／失敗／略過 | 測試耗時 | TIME_WAIT 開始／峰值／結束 |
| --- | --- | --- | --- |
| 修改前基準 | 266／0／0 | 約 20 秒 | 0／4837／4837 |
| 修改後連續第 1 輪 | 269／0／0 | 約 13 秒 | 0／544／544 |
| 修改後連續第 2 輪 | 269／0／0 | 約 13 秒 | 544／1088／1088 |

資料支持「停用 pooling 造成大量短連線與 TIME_WAIT 壓力」的機制與修正效果；本輪未刻意重現先前的 port 耗盡，因此仍不宣稱已排除所有可能根因。測試後殘留 pasto_it_* schema 為 0。

實際執行 Release build（0 warning／0 error）、format 與 format verify、diff check。相關測試 11 例通過；修正後另兩次完整測試也均為 269 passed，但因工具等待讓 TIME_WAIT 歸零，以上表的緊接兩輪作為連續執行證據。完整測試命令沿用 `dotnet test Pasto.slnx -c Release --no-build --no-restore`，加上各輪 TRX logger。

保留的失敗／量測工具紀錄：第一輪相關測試為 10 passed／1 failed，重用測試拿到不同 PID；原因是初始化同時保留建 schema 與 Migration 的兩條連線。已先歸還初始化 lease，斷言未放寬，後續 11 例通過。基準量測包裝程式曾因無法取得 Process.ExitCode 而回傳 1，但 TRX 是 Completed、266 passed；後續包裝程式改以 TRX outcome／counters 判定，不將工具狀態誤列為測試失敗。

TRX 與取樣 JSON／stdout／stderr 保存在被忽略的 TestResults：s3-01-pool-baseline、s3-01-pool-related（失敗）、s3-01-pool-related-fixed、s3-01-pool-fixed-full-1／2、s3-01-pool-consecutive-1／2。本輪已由審查方獨立 review（見上節），未開始 S3-02。以下保留先前交付的歷史紀錄。

## S3-01 驗證紀錄（2026-11-18）

新增 ItemWriteApiTests 38 例（34 個真 DB，含 3 個真 Kestrel；4 個不連 DB）。覆蓋原文保留、8192／8193 UTF-8 bytes、空白／NUL／不成對 surrogate、拒絕授權欄位、401／403／404／409、重複刪除、Participant 新增／刪除配額分離、關房共用刪除配額、驗證前粗限流、提交前／後故障與不重試。300 筆前置資料透過正式 AddItem 建立。

- 首次 Build 曾失敗：新測試直接組裝 CreateRoom 時漏帶 logger；已改用既有 TestRoomCreation helper，後續 Release Build 為 0 warning／0 error。
- 初版相關 32 例通過，TRX：s3-01-related.trx。補齊容量、真 Kestrel 與設定測試後共 38 例。
- 首次完整套件：265 passed／1 failed／0 skipped，TRX：s3-01-full.trx。失敗為既有 ParticipantApiTests.LastParticipantSlot_TwoHttpRequestsOnlyOneCommits_Then1001stIs409，在正式 Join 前置資料建立時發生 NpgsqlException → SocketException：「一次只能用一個通訊端位址」。
- 觀察本機對 55432 約 8,600 個 TIME_WAIT，降到約 3,300 後重跑：264 passed／2 failed／0 skipped，TRX：s3-01-full-after-port-release.trx。同一既有測試及新增 FullRoom_Returns409_WithoutChangingCounters 的前置資料建立發生相同 socket 錯誤。未修改正式交易、測試斷言或測試資料建立方式來繞過錯誤。
- 這是連線建立失敗；TIME_WAIT 壓力是觀察到的環境線索，尚不能僅據此宣稱已確認所有根因。兩次失敗紀錄均保留於測試專案內被忽略的 TestResults 目錄。
- 後續觀察：沒有其他測試程序同時執行；TIME_WAIT 已降至約 600。以沙箱外環境獨立執行相關測試，38 passed／0 failed／0 skipped，約 10 秒（s3-01-related-isolated.trx）。同環境完整回歸 266 passed／0 failed／0 skipped，約 21 秒（s3-01-full-unrestricted.trx）。測試後殘留 schema 為 0。執行權限與連線壓力均有變化，不能據此單獨認定任一項為唯一根因；沒有修改測試隔離、連線池設定或併發斷言。

執行命令：`dotnet build Pasto.slnx -c Release --no-restore`、`dotnet test Pasto.slnx -c Release --no-build --no-restore --logger 'trx;LogFileName=…'`；初版相關測試另加 `--filter FullyQualifiedName~ItemWriteApiTests`。已執行 dotnet format 及 `--verify-no-changes`。S3-02 快照列表與 S8 壓測尚未執行，S3-01 待獨立 review。以下 S2 紀錄為歷史結果。

S2-03 實測（2026-11-18）：新增 RoomStateApiTests 33 例（29 個真 DB、4 個不連 DB）。涵蓋 metadata 契約與快照一致性、Bearer／同房／權限／有效性順序、兩層共用限流、驗證後被清理的競爭、HTTP 併發關房、commit 前回滾與 commit 後結果不確定。併發關房以 pg_blocking_pids 確認等鎖；既有 HTTP 提交故障 hook 搬至 TransactionTestHooks 共用，未改行為。

本輪實際執行 Release build、相關測試、完整測試、format 與 format verify、git diff --check。Build 0 warning／0 error；相關 33 passed、完整 228 passed，皆 0 failed／0 skipped；測試後 pasto_it_* schema 為 0。測試命令使用 `dotnet test Pasto.slnx -c Release --no-build --no-restore`，相關套件另加 `--filter FullyQualifiedName~RoomStateApiTests`。TRX 分別為 TestResults/s2-03-related.trx 與 s2-03-full.trx（測試專案內，已忽略）。本輪沒有測試失敗；未執行 S8 負載測試，S2-03 尚待獨立 review，不將實作方結果描述為審查方證據。

更新：2026-11-18，S2-03 metadata／關房 API。DB 案例使用正式 Migration 與隔離 schema；HTTP／host 測試載入實際 Program，body 上限另用真 Kestrel／loopback TCP 驗證。

## 1. 環境與相依

| 項目 | 固定版本／用途 |
| --- | --- |
| .NET SDK | 10.0.402，見 root global.json |
| PostgreSQL | 18.6，見 compose.yaml |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3，正式專案的 provider |
| EF Core／Relational／Design | 10.0.4，Design 為工具相依且 PrivateAssets=all |
| dotnet-ef | 10.0.4，本機工具，見 dotnet-tools.json |
| Microsoft.NET.Test.Sdk／xunit／runner | 18.0.1／2.9.3／3.1.5 |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.13，僅測試相依，與本機 ASP.NET Core runtime 對齊 |

兩個專案都有 `packages.lock.json`；測試透過 ProjectReference 使用正式 provider，不再維護測試用 DbContext 或 DDL。未新增 Testcontainers、SQLite、InMemory 或通用 Repository。

## 2. 啟動本機 PostgreSQL

在 repo 根目錄執行。若已有 `.env` 或資料庫，直接沿用，勿覆蓋或重建資料。

```powershell
Copy-Item .env.example .env
# 編輯 .env，填入自己的 PASTO_POSTGRES_PASSWORD。
docker compose config --quiet
docker compose up -d --wait
docker compose ps
```

主機 `127.0.0.1`、port `55432`、DB `pasto_test`、使用者 `pasto_test`。Docker CLI 找不到時，重開終端機或確認 PATH。Compose 健康檢查僅確認 server 接受連線，不代表密碼／Migration／測試已驗證。

一般停止用 `docker compose down`，named volume 保留資料。`docker compose down --volumes` 會刪除整個 DB，不當作一般停止指令。初始化後修改 `.env` 不會改既有 DB 密碼。

## 3. 建置與測試

`.env` 不會自動匯入 PowerShell 或 ASP.NET Core。請另外設定測試連線，使用與 DB 相同的密碼，不將實際連線字串存入 repo 或日誌。

```powershell
$env:PASTO_TEST_CONNECTION_STRING = 'Host=127.0.0.1;Port=55432;Database=pasto_test;Username=pasto_test;Password=<本機測試密碼>'
dotnet tool restore
dotnet restore Pasto.slnx --locked-mode
dotnet build Pasto.slnx --configuration Release --no-restore
dotnet test Pasto.slnx --configuration Release --no-build --no-restore
dotnet format Pasto.slnx --no-restore --verify-no-changes
```

目前共 269 個測試，完整通過（201 個真 DB 案例〔含 3 個真 Kestrel〕、57 個不連 DB 的 HTTP／host 案例〔含 7 個真 Kestrel〕、10 個文字輸入或預先取消、1 個憑證／房碼生成測試）。DB 案例未提供連線／DB 不可用時失敗，不 skip。測試只接受 loopback，且 DB 名稱須以 `_test` 結尾，執行者仍須確認是專用測試 DB。

| 案例 | 驗證內容 |
| --- | --- |
| CreatorIntegrityTests（3 例） | 正式 CreateRoom 成功、跨房 Creator／缺房主在 commit 被拒絕且回滾，原房保留 |
| RoomCreationTests（11 例） | 已用碼／冷卻碼碰撞、重試上限、非房碼 unique 錯誤不重試、寫入後／commit 前失敗、取消、commit 後結果遺失、兩交易房碼競爭 |
| OwnershipAndCascadeTests（5 例） | Owner 同房正反例、帶文字實際刪房 cascade／保留碼／他房保留、單刪 Creator／被引用保留碼拒絕 |
| RoomSecretsTests（1 例） | 六碼格式、128-bit 原始隨機值的 Base64URL 格式與 SHA-256 編碼一致性，不以抽樣宣稱密碼學強度 |
| ItemAdditionTests（29 例） | 文字驗證、同房／容量／失效檢查、一般參與者、真 DB 鎖競爭與取消／逾時、統計／版本、提交前回滾及提交後例外、非 55P03 故障不轉忙碌；見 S1-04 工作項 |
| ItemDeletionTests（24 例） | 刪自己的文字、房主不能刪他人、檢查順序、跨房／不存在、同筆／不同筆併發、新增與刪除交錯、滿房容量釋放、鎖逾時／取消、提交前後故障 |
| RoomClosureTests（22 例） | Creator 關房與優先檢查、保留統計／原期限／冷卻碼、併發關房輸方不可用、關房與新增／刪除兩種順序、關房回滾後新增成功、逾時／取消與提交前後故障 |
| RoomExpiryTests（21 例） | 三條路徑的到期前一微秒／相等／後一微秒、DeletedAt；真實等鎖後到期拒絕，驗證不能用交易起始時間 |
| RoomApiTests（39 例） | 正式建房 HTTP、偽造 Host、DI／啟動選項、pool 脫敏、錯誤表與日誌分級、真 PG provider 錯誤脫敏、代理／限流／429、提交前後取消或故障 |
| ParticipantApiTests（29 例） | Join 正規化／後端身分、Bearer challenge body、跨房錯誤順序、me 恢復、憑證查詢前與驗證後限流、1000 人競爭、等鎖後失效、提交前後故障 |
| RequestBodyLimitTests（7 例） | 真 Kestrel 的 32 KiB body 設定與 Content-Length／chunked 實際拒絕；413 code、no-store、未呼叫 Join Store |
| Migration_PreservesCompositeForeignKeysAndCascadeDefinitions | pg_constraint 的五條 FK 欄位／參照、NO ACTION／CASCADE、deferred 定義 |
| Migration_CreatesChecksAndRequiredIndexes | 八條 CHECK、部分索引、排序／Owner／唯一索引實際定義 |
| Migration_CanReapplyAndRollbackWithoutModelDrift | snapshot 無模型漂移；重跑不重建、Down 可拆循環、Up 可再套用 |
| MigrationHistory_IsIsolatedPerTestSchema | 兩個 schema 各自建表／記 Migration；回退其中一個不影響另一個 |

Creator 案例已改跑正式 CreateRoom／RoomCreationStore，移除原本的建房寫入 helper。`TestRoomCreation` 只組裝正式服務；故障／非法資料由測試 interceptor 注入，產品沒有測試專用開關。Owner／cascade 測試的文字資料準備仍為 FK／刪除 fixture；S1-04 的容量前置資料全部使用正式 AddItem，不複製寫入邏輯。

## 4. Migration 與 host 的操作

正式模型：[PastoDbContext](../src/Pasto.Server/Infrastructure/Persistence/PastoDbContext.cs)。初始 Migration 與 snapshot 位於同層的 `Migrations/`。

工具使用獨立 design-time factory，不啟動 Web host。連線必須明確指定；單純產生 Migration／SQL script 不會連 DB，`database update` 才會變更指定 DB。不要把共用／正式 DB 當練習目標。

```powershell
$env:ConnectionStrings__Pasto = $env:PASTO_TEST_CONNECTION_STRING
dotnet ef migrations has-pending-model-changes --project src/Pasto.Server
dotnet ef migrations script --project src/Pasto.Server --output src/Pasto.Server/obj/migration.sql
# 確認是專用目標 DB 並審查 SQL 後，才手動執行：
# dotnet ef database update --project src/Pasto.Server
```

手動 update 使用連線預設 schema，與測試的 GUID schema 分開；測試不需要先跑 update。正式 host 也使用 `ConnectionStrings__Pasto`，啟動時不自動 Migrate。S2-01 已有 POST /api/rooms，其餘產品 endpoint 尚未提供。建房核心見 [S1-03 導讀](s1-03-room-creation.md)，HTTP 設定見 [S2 工作項](s2-api-identity.md)。

EF 能追蹤的 CHECK／部分索引保留在 mapping 與 snapshot，初始 Migration 依 ADR 0003 以明確 SQL 建立；DEFERRABLE 不在 EF 模型內，日後任何重建這兩條 FK 的 Migration 都須手動保留並跑 catalog 測試。歷史 Migration 不引用目前 Domain 常數，避免規則變更影響舊 DDL。

相較 S1-01，EF 依 Creator 複合 FK 慣例額外建立 `IX_rooms_id_creator_participant_id (id, creator_participant_id)`。本階段刻意保留，不改變外鍵保證；因 `id` 已是 PK，這個索引的查詢效益可能有限。測試固定四張產品表的 13 個索引，加上 Migration history 的 PK，共 14 個索引名稱，後續增刪須明確審查。產生的 `obj/migration.sql` 已受既有 ignore 規則排除，只供檢閱，正式來源仍為 Migration。

工具行為參考：[EF design-time factory](https://learn.microsoft.com/en-us/ef/core/cli/dbcontext-creation)、[自訂 Migration](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/managing)。

## 5. 隔離、清理與實測界線

每例 `pasto_it_<32位GUID>`，search_path 只指向自身 schema，包含 `__EFMigrationsHistory`。先建 schema，再由 Migrator 管理交易；初始化失敗會清自己的 schema，正常結束由 await using 清理。不用 EnsureCreated／EnsureDeleted、不 DROP DATABASE、不清 public，也不掃刪其他測試遺留 schema。

若程序被強制終止可能遺留 schema。DBeaver 可連相同 DB，先確認名稱來源後再人工處理；正常完成看不到永久測試表是預期行為。

S1-02 當時實測：Windows、SDK 10.0.402、runtime 10.0.13、Docker PostgreSQL 18.6，Release 六個案例通過。之前曾被 Windows Smart App Control 封鎖 DLL（0x800711C7）；環境調整後可正常執行，不是測試斷言失敗。

### S1-02 審查後驗證紀錄（2026-11-08）

- 測試當時基準 commit：`211a2a6`；分支：`feature/s1-02-persistence`。當時結果對應未提交的工作目錄；後續實作提交為 `6846558`，並經 PR #1 合併至 `00a9890`。提交前只額外移除 DbContext 檔尾空白行，無邏輯變更；本機確認 merge tree 與 `6846558` 相同。
- `dotnet restore Pasto.slnx --locked-mode`：通過。
- `dotnet build Pasto.slnx -c Release --no-restore`：通過，0 warning／0 error。
- `dotnet format Pasto.slnx --no-restore --verify-no-changes`：通過。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore`：連續兩輪各 6 passed／0 failed／0 skipped，維持 xUnit 預設的跨 class 並行。
- 測試前後唯讀查詢 `^pasto_it_[0-9a-f]{32}$`：皆為 0 個 schema，沒有新增殘留。
- 原始結果保存在本機 `tests/Pasto.IntegrationTests/TestResults/s1-02-verified-run1.trx` 與 `s1-02-verified-run2.trx`；TestResults 已 ignore，不納入版本控制。
- 擴充索引集合斷言時，首次漏列 `PK___EFMigrationsHistory`，造成 1 failed／5 passed；補齊後才取得上述兩輪通過結果。首次失敗紀錄保留於同目錄 `s1-02-review-run1.trx`。

Claude 本輪僅做靜態審查；上述測試由實作方重跑，不宣稱為獨立測試驗證。S1-02 現已 commit／merge，但整個 S1 尚未完成。維護導讀與合併紀錄見 [S1-02 報告](s1-02-maintenance-report.md)。

### S1-03 驗證紀錄（2026-11-08）

- 基準 `00a9890`；分支 `feature/s1-03-create-room`，尚未提交。沿用 schema，沒有新增 Migration、套件或基礎設施。
- Release Build：0 warning／0 error；格式檢查通過。最終完整測試 23 passed／0 failed／0 skipped；測試前後均為 0 個測試 schema。
- 鎖定版本還原通過；首次在沙箱內查 NuGet 弱點資料遇到 NU1900（網路受限），取得連線權限後重跑成功，沒有關閉 audit 或修改套件版本。
- 首次 Build 遇到 xUnit2031（Assert.Single 前不應先 Where），已改用 predicate overload，沒有降低斷言。
- 首次 DB 測試 22 passed／1 failed：跨房 Creator interceptor 誤改 entity，導致新房主 ID 跟著變成既有 ID，先觸發 `PK_room_participants`。改為在同交易用參數化 SQL 只污染已寫入 Room 的 Creator 欄位，保留 FK 啟用；最終驗證為 commit 的 `fk_rooms_creator`／23503。
- 本機 TRX：`tests/Pasto.IntegrationTests/TestResults/s1-03-initial.trx`（首次失敗）、`s1-03-verified.trx`（修正後通過），皆受 ignore 規則排除。
- 提交後例外的案例會保留已 commit 的三筆資料，並確認沒有自動重試；這個結果不應被說成 rollback 失敗。

### S1-03 審查後補驗證（2026-11-09）

- Claude 靜態審查無 P0～P2；核對既有 TRX，未獨立執行測試。
- F1 保留 commit 的 CancellationToken，在程式註解與導讀明確記錄結果不確定語意；新增已提交後收到取消例外的測試，不宣稱模擬真實網路競爭。
- Release Build（0 warning／0 error）、格式檢查通過；完整測試 24 passed／0 failed／0 skipped；測試前後均為 0 個測試 schema。
- TRX：`tests/Pasto.IntegrationTests/TestResults/s1-03-review-20261109.trx`，保留先前 23 例的紀錄。
- F2 尚無 SQL 往返／效能量測，保留預設 savepoint，延至 S8 評估。Program.cs 的 DI／ValidateOnStart 與 HTTP 取消處理測試列入 S2。

尚未驗證：新增／刪除／關房的正式交易、文字容量競爭與計數一致性、期限邊界、REST／UI／SignalR、授權與完整安全／負載測試。S1-03 的房碼碰撞競爭不等於 S1-04 的文字併發已完成。

### S1-04 Patch 1 驗證紀錄（2026-11-16）

- 從 main `1ad0e2b` 建立 `feature/s1-04-item-transactions`；新增 AddItem／ItemAdditionStore，無 schema／套件變更。
- `dotnet build Pasto.slnx -c Release --no-restore`：通過，0 warning／0 error。
- `dotnet format Pasto.slnx --no-restore --verify-no-changes` 與 `git diff --check`：通過。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore --logger "trx;LogFileName=s1-04-patch1-full.trx"`：51 passed／0 failed／0 skipped；新增 27 例含 19 個真 DB 案例及 8 個輸入／預先取消案例。
- 測試完成後唯讀查詢 `^pasto_it_[0-9a-f]{32}$`：0 個 schema，無測試殘留。
- 首次 Build 缺少測試的 Persistence using（CS0246），補齊後成功。
- 首次針對性測試因執行指令未接受 `.env` 設定名稱旁空白，未帶入密碼：8 passed／17 failed；修正本機指令解析後，24 passed／1 failed。該失敗是預期例外型別不符：Npgsql EF 預設策略以 InvalidOperationException 包住 55P03；改為明確檢查內層 PostgreSQL SQLSTATE 並驗證只嘗試一次，未改產品重試行為。
- 失敗 TRX 保留為 `s1-04-patch1-initial.trx`、`s1-04-patch1-verified.trx`；最終為 `s1-04-patch1-full.trx`，皆在忽略的本機 TestResults。
- 待使用者獨立 review；未執行刪除／關房、S1-05 精確期限邊界、HTTP／SignalR／負載測試，因不在 Patch 1 範圍。DI 的 host 層驗證同既定計畫留 S2。

S1-01 的歷史教學見 [入門導讀](s1-01-dotnet-postgresql-guide.md)，其中舊 DDL／mapping 不是現行實作。

### S1-04 Patch 1 審查修正（2026-11-16）

- Review Agent（Claude）對 `6612d06` 相對 `1ad0e2b` 僅做獨立靜態審查：P0～P2 共 0，P3 共 3；未重跑 Build／測試。F1 增量亦經 Claude 靜態審查，No Actionable Findings，使用者已核准提交並開始 Patch 2。
- F1：將房間鎖取得階段的 55P03 轉成 Application RoomBusyException，保留原例外作 InnerException 供診斷。API 只需辨識 Application 型別。未擴大到 commit 例外，也不自動重試。
- F2 延至 Patch 2 最小抽取；F3 尚無偶發失敗證據，保留原測試並記錄風險，詳見 S1-04 工作項。
- `dotnet build Pasto.slnx -c Release --no-restore`：0 warning／0 error；`dotnet format Pasto.slnx --no-restore --verify-no-changes`、`git diff --check`：通過。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore --filter FullyQualifiedName~ItemAdditionTests --logger "trx;LogFileName=s1-04-patch1-review.trx"`：29 passed／0 failed／0 skipped，含新增 2 個非逾時例外案例。
- 本輪未重跑其餘 24 例：變更範圍僅新增交易的鎖查詢例外轉換，相關測試已涵蓋成功、逾時、取消、其他故障及 commit 前後語意。初次交付的 51 例完整結果仍屬 `6612d06`，不將它宣稱為修正後完整結果。

### S1-04 Patch 2 驗證紀錄（2026-11-17）

- F1 已由使用者核准並提交 `09d277f`，本 patch 相對此基準。F2 完成 Domain 規則／共用鎖房流程抽取，房間層級例外統一到 Application.Rooms；無 schema、Migration 或套件變更。
- `dotnet build Pasto.slnx -c Release --no-restore`：通過，0 warning／0 error。
- `dotnet format Pasto.slnx --no-restore --verify-no-changes`、`git diff --check`：通過。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore --filter "FullyQualifiedName~ItemDeletionTests|FullyQualifiedName~ItemAdditionTests" --logger "trx;LogFileName=s1-04-patch2-related.trx"`：53 passed／0 failed／0 skipped。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore --logger "trx;LogFileName=s1-04-patch2-full.trx"`：77 passed／0 failed／0 skipped。既有 53 例與新增 24 例均通過，不將之前未執行的 53 例完整回歸說成已在 F1 當時執行。
- 新增案例 23 個真 DB、1 個預先取消；DB 文字資料經正式 AddItem，300 筆容量案例沒有測試專用寫入 helper。重刪與新增／刪除兩種順序先觀察 `pg_blocking_pids` 再放行持鎖交易；不同筆刪除使用查詢前 barrier。
- TRX 留在忽略的本機 TestResults；本輪 Build／測試無失敗。尚待獨立 review，不宣稱 Claude 已審查 Patch 2。
- 完整測試後唯讀查詢 `^pasto_it_[0-9a-f]{32}$`：0 個 schema，無測試殘留。
- 關房競爭、精確期限邊界、host DI／HTTP、SignalR、負載測試仍依後續階段進行；未在本輪執行。

### S1-04 Patch 3 驗證紀錄（2026-11-17）

- 基準 `c060864`。Claude 對 Patch 2 僅靜態審查：P0～P2 共 0、P3 共 1；未重跑 Build／測試。P3 的重複工具已原樣搬到 TransactionTestHooks，觀察窗與注入點未改變；兩份 AfterSave 的 token 檢查差異保留為兩個型別。產品新增 CloseRoom／RoomClosureStore 與 DI，無 schema／套件變更。
- `dotnet build Pasto.slnx -c Release --no-restore`：通過，0 warning／0 error。
- `dotnet format Pasto.slnx --no-restore --verify-no-changes`、`git diff --check`：通過。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore --filter FullyQualifiedName~RoomClosureTests --logger "trx;LogFileName=s1-04-patch3-related.trx"`：22 passed／0 failed／0 skipped。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore --logger "trx;LogFileName=s1-04-patch3-full.trx"`：99 passed／0 failed／0 skipped，包含共用工具搬移後原新增／刪除斷言的回歸驗證。
- 新增案例 21 個真 DB、1 個預先取消；文字均經正式 AddItem，身分 fixture 僅準備尚無 Join use case 的 Participant。等待鎖後關房拒絕與反向寫入先成功的測試均由 pg_blocking_pids 證實競爭；close 回滾再由等待新增提交，驗證 DeletedAt／Version 未殘留。
- TRX 位於忽略的本機 TestResults；本輪 Build／測試無失敗，Patch 3 尚待獨立 review。
- 完整測試後唯讀查詢 `^pasto_it_[0-9a-f]{32}$`：0 個 schema，無測試殘留。
- 未執行精確等於期限／等待鎖後到期的 S1-05 邊界、host DI／HTTP、SignalR、Worker、負載測試，仍依既定後續階段處理。

### S1-04 審查方獨立實測（2026-11-17）

- 審查方（Claude）在 `feature/s1-04-item-transactions` 的 `6781f81`、乾淨工作目錄上獨立執行並回報；這次有實測，與前述僅靜態審查的紀錄分開。
- `dotnet build Pasto.slnx -c Release --no-restore`：成功，0 warning／0 error。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore`：99 passed／0 failed／0 skipped，測試回報約 18 秒。
- `dotnet format Pasto.slnx --no-restore --verify-no-changes`：通過；測試後 `pasto_it_*` schema 查詢為 0 個。
- TRX：`tests/Pasto.IntegrationTests/TestResults/s1-04-claude-review-full.trx`，已受 ignore 規則排除。Codex 實際核對 counters：total／executed／passed 均為 99，failed／notExecuted 均為 0；起訖為台灣時間 21:01:22～21:01:41。Build／format／schema 結果依審查方提供的紀錄，不冒稱本輪由 Codex 再次執行。
- 使用者已核准合併 S1-04。後續只補本文件與進度狀態，不變更已驗證產品／測試程式；未因文件更新重跑 Build／測試。
- 下一步從合併後的 main 建立 `feature/s1-05-expiry-boundaries`，處理既定期限邊界；不代表 S1 或 MVP 已全部完成。

### S1-05 驗證紀錄（2026-11-17）

- 已 pull GitHub main 的 PR #3 merge `2853cda`，S1-05 分支快轉至同一基準。本輪只有 RoomExpiryTests 與文件變更，不改產品程式、schema、Migration 或套件。
- `dotnet build Pasto.slnx -c Release --no-restore`：通過，0 warning／0 error。
- `dotnet format Pasto.slnx --no-restore --verify-no-changes`、`git diff --check`：通過。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore --filter FullyQualifiedName~RoomExpiryTests --logger "trx;LogFileName=s1-05-related.trx"`：21 passed／0 failed／0 skipped。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore --logger "trx;LogFileName=s1-05-full.trx"`：120 passed／0 failed／0 skipped，含既有 deferred FK 正反例、rollback／commit 故障及併發案例的回歸。
- 18 例以測試 interceptor 在鎖後時間查詢點讀取 persisted ExpiresAt 加偏移，精確固定比較語意；3 例不替換時間查詢，先觀察 pg_blocking_pids，再由持鎖 fixture 以 DB 時間設定到期並提交。後者斷言等待方 xact_start 早於 ExpiresAt，正式操作仍被拒絕，可抓出誤用 now() 或等鎖前取時間。
- 前者不是自然牆鐘命中微秒的證據；後者才驗證真實 clock_timestamp／鎖等待。測試限制與 S1 出口條件對照見 [S1-05](s1-05-expiry-boundaries.md)。
- TRX 位於忽略的本機 TestResults；本輪 Build／測試無失敗，S1-05 待獨立 review，未宣稱審查方已重跑。
- 完整測試後唯讀查詢 `^pasto_it_[0-9a-f]{32}$`：0 個 schema，無測試殘留。
- 未開始 S2；Join、HTTP／host DI、SignalR、Worker／保留碼回收與壓測仍依後續階段處理。

### S2-01 驗證紀錄（2026-11-17）

- 基準 main `781470a`，分支 `feature/s2-01-create-room-api`。使用者核准 Microsoft.AspNetCore.Mvc.Testing 相依及 metadata API 歸 S2；開工前五項決策記錄於 [S2 工作項](s2-api-identity.md)。本次只交付 S2-01。
- 新增測試套件 10.0.13 與 test lock file；首次沙箱還原遇 NU1301（網路權限不足），取得權限後正常還原，未停用 audit。
- `dotnet restore Pasto.slnx --locked-mode`：通過。
- `dotnet build Pasto.slnx -c Release --no-restore`：0 warning／0 error。
- `dotnet format Pasto.slnx --no-restore --verify-no-changes`、`git diff --check`：通過。
- 初次 smoke：2 passed；擴充 HTTP／host 測試：36 passed；再補 429 body 與 commit 前取消測試後完整執行 `dotnet test Pasto.slnx -c Release --no-build --no-restore --logger "trx;LogFileName=s2-01-full.trx"`：158 passed／0 failed／0 skipped。
- 本輪新增 38 例（5 個真 DB、33 個 HTTP／host 無 DB）；使用實際 Program 的註冊與 middleware，只為故障／限流情境替換 Store 或注入 EF interceptor。並非另一套測試用 DI 或寫入 helper。
- 真 DB 的提交後取消／故障案例保留已 commit 的三筆資料，回 503 且沒有 Retry-After，不自動重送；提交前取消完整回滾。這不宣稱真實網路競爭或 client 已斷線後仍能送達回應。
- 本輪 Build／測試無失敗；TRX：s2-01-api-smoke.trx、s2-01-api-first.trx、s2-01-full.trx，位於忽略的 TestResults。完整測試後隔離 schema 查詢：0 個。
- 尚待獨立 review，未宣稱審查方已重跑。Join／身分驗證、me／metadata／關房 HTTP、後續限流及 SignalR／Worker／壓測仍未執行。

### S2-01 審查增量

- Claude 靜態審查 `781470a..a614f61`：P0～P2 共 0、P3 共 1，未重跑 Build／測試。日誌改為 4xx Information、503 Warning、500 Error；500 保留例外型別與無檔案路徑的實際 frames，不記訊息。
- 新增真 PostgreSQL 房碼衝突的 provider 日誌驗證；原 14 個錯誤案例亦斷言日誌等級，500 診斷與敏感 sentinel 不洩漏。相關 HTTP／host 測試 39 passed，TRX 為 s2-01-review-verified.trx。
- 首次 Build 因 using 插入位置錯誤遇 CS1529，修正後 0 warning／0 error；首次隨後執行的 no-build 結果仍是舊 38 例（s2-01-review.trx），不視為修正後證據。
- S2-02 會驗證 challenge body 的 ParticipantUnauthorized，不能僅檢查狀態碼。S2-01 未經使用者核准不合併／推送；審查方獨立實測仍可針對此增量提交進行。

### S2-02 驗證紀錄

- S2-01 日誌修正保留提交 `58e7926`（相關 39 例通過，未合併）。S2-02 的分支為 feature/s2-02-join-identity，由此基準建立，沒有 schema／Migration 或套件變更。
- `dotnet build Pasto.slnx -c Release --no-restore`：0 warning／0 error；`dotnet format Pasto.slnx --no-restore --verify-no-changes`、`git diff --check`：通過。
- 首輪 `--filter FullyQualifiedName~ParticipantApiTests`：25 passed／0 failed／0 skipped（s2-02-related.trx）。補上跨 IP 全站讀取 admission、驗證器故障、Join 等鎖後關房／到期後，完整執行 `dotnet test Pasto.slnx -c Release --no-build --no-restore --logger "trx;LogFileName=s2-02-full.trx"`：188 passed／0 failed／0 skipped，含全部 29 個新案例。
- 999 人的前置資料全經正式 JoinRoom，沒有測試專用 Participant 寫入邏輯；兩個 HTTP host 以 pg_blocking_pids 證明競爭，只有一個加入成功，總數 1000，輸方與後續第 1001 人均 409，內容版本不變。
- Join 等鎖後關房使用正式 CloseRoom；等鎖後到期用持鎖 fixture 調整 DB 時間並斷言等待方 xact_start 早於 ExpiresAt，正式 Join 仍被拒絕。commit 前取消回滾、commit 後故障保留 Participant，不自動重送。
- 401 的狀態、ParticipantUnauthorized、correlationId、WWW-Authenticate、no-store 均經真 HTTP pipeline 檢查；跨房的 recovery Store 被替換成不應呼叫的 sentinel，證明拒絕發生在讀目標房之前。相同房間失效回 404，驗證器仍辨識到原身分。
- 憑證前 IP／全站 admission 用計數驗證器測呼叫次數；不宣稱這組計數 spy 是 DB 查詢效能證據。實際 hash／身分查詢另由真 DB Join／me 案例覆蓋。
- TRX 位於忽略的 TestResults；本輪 S2-02 Build／測試無失敗，測試後 0 個殘留 schema。這是實作方結果，S2-02 尚待獨立 review；審查方未獨立重跑 S2-01／02。
- 尚未提供 metadata／關房 HTTP（S2-03）、文字 API／讀寫限流（S3）、Hub／重連（S5）、清理／分層防護／負載測試。沒有公開部署。

### S2-02 審查修正（2026-11-18）

- Claude 僅靜態審查，兩項 P2；未重跑測試。Finding 1 設定 Kestrel MaxRequestBodySize=32768，並修正 StatusCodePages 對 413 的 code。Finding 2 使用者選 (a) 維持 quota，風險與公開部署前必要工作已寫入 S2／S7，不宣稱已緩解。
- `dotnet build Pasto.slnx -c Release --no-restore`：0 warning／0 error；格式與差異檢查通過。
- 真 Kestrel 首輪測試 4 passed／2 failed（s2-02-body-limit.trx）：錯誤假設 chunked 在 JSON bytes 未超限時一定通過。核對 ASP.NET Core 10.0.13 原始碼確認 Kestrel 會計入分塊控制位元組；保留安全上限，明確記錄較保守的 chunked 行為，並保留對應拒絕案例、增加較小 chunked 可通過的案例。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore --filter FullyQualifiedName~RequestBodyLimitTests --logger "trx;LogFileName=s2-02-body-limit-verified.trx"`：7 passed／0 failed／0 skipped。真 TCP 只監聽 loopback 動態埠，沒有新增套件或 TestServer 假設。
- `dotnet test Pasto.slnx -c Release --no-build --no-restore --logger "trx;LogFileName=s2-02-review-full.trx"`：195 passed／0 failed／0 skipped。
- TRX 保留在忽略的 TestResults；修正增量仍待獨立 review，不將這次執行說成 Claude 的獨立實測。未開始 S2-03、S7 防護或 S8 壓測。
- 完整測試後唯讀查詢隔離 schema：0 個，沒有測試殘留。
