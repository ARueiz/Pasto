
# Pasto — AI Agent Guidelines

## 1. Project Overview

Pasto 是多人即時共享剪貼簿 Web Application。

MVP 支援：
- 匿名建立與加入房間
- 透過連結分享房間
- 多筆文字內容共享
- SignalR 即時同步

專案同時用於學習：
- System Design
- Scalability
- Concurrency
- AI-assisted Development

本專案的優先順序：
1. Correctness & Security
2. Simplicity & Maintainability
3. Testability
4. Performance & Scalability

避免為了未來可能發生的需求而過度設計。

## 2. Tech Stack

- Frontend: React + TypeScript + Vite
- Backend: ASP.NET Core (.NET 10), SignalR
- Database: PostgreSQL + EF Core
- Infrastructure: Docker Compose
- Architecture: Modular Monolith

除非有明確需求與核准，不得自行更換技術或引入新的架構元件。

## 3. Architecture Guidelines

- 採用 API、Application、Domain、Infrastructure 分層。
- 保持依賴方向清楚，避免不必要的跨層耦合。
- PostgreSQL 為 Source of Truth。
- REST API 負責資料操作，SignalR 負責即時通知。
- 資料交易成功後才發送通知。
- Client 重新連線後應能從 Server 重新同步。
- 考慮 Concurrency、Index、Pagination、Rate Limiting 與 Security。
- 避免未經證實的效能優化與過早抽象化。
- 未有明確需求前，不引入 Microservices、Redis 或 Message Queue。
- 優先維持程式碼簡單、可讀且可測試。

## 4. Coding Guidelines

### Backend
- 使用 async/await 與 CancellationToken。
- 遵循 Dependency Injection。
- 使用 Structured Logging。
- 明確處理 Exception 與資源釋放。
- 注意資料一致性及併發操作。
- 避免不必要的資料庫查詢與記憶體配置。

### Frontend
- 使用 React Functional Components 與 Hooks。
- 使用 TypeScript，避免不必要的 any。
- 遵循既有 Component 與 State Management 慣例。
- 妥善處理 Loading、Error 與 Reconnection 狀態。
- 避免不必要的 Re-render 與複雜抽象。

### General
- 遵循 Repository 既有命名與程式風格。
- 優先修改既有實作，而非建立平行機制。
- 避免不必要的 Dependencies。
- 注意 Input Validation、Security 與敏感資訊保護。
- 不得硬編碼 Secrets、Tokens 或 Credentials。
- 重要商業邏輯應有自動化測試。

## 5. Context Management

Agent 執行任務時，應遵循以下原則：

1. 先閱讀本文件與任務直接相關的文件。
2. 優先使用檔名搜尋、程式碼搜尋及局部檔案讀取。
3. 僅載入完成任務所需的程式碼與上下文。
4. 不因缺乏必要資訊而猜測既有行為。
5. 不預設需要閱讀整個 Repository。
6. 不重複讀取已知且未變更的內容。
7. 詳細設計以現有文件為準，不複製到本文件。

重要文件：
- docs/requirements.md
- docs/architecture.md
- docs/decisions/（若存在）

如果文件與實際程式碼不一致，應指出差異，
不得自行假設任一方必然正確。

## 6. Task Scope & Planning

每次任務應有明確的目標與完成條件。

開始修改前：
1. 確認任務目標與預期行為。
2. 識別受影響的程式碼及測試。
3. 判斷是否涉及架構、安全或資料契約變更。
4. 選擇最小且合理的修改方案。

一般的小範圍修改可直接實作。

若涉及下列情況，須先提出方案、理由及
Trade-offs，並等待確認：
- Architecture 或 Module Boundary 變更
- Database Schema 或 Migration 變更
- Public API 或 SignalR Contract 變更
- Authentication / Authorization 變更
- 新增主要 Dependencies 或 Infrastructure
- 大範圍 Refactoring
- 可能造成資料遺失或不相容的修改

不得擅自：
- 擴大需求範圍
- 修改無關功能
- 覆蓋使用者或其他 Agent 的既有修改
- 更動既定技術決策
- 建立缺乏實際需求的抽象層

## 7. Verification Strategy

所有程式碼修改都需要與風險相符的驗證。

### Low Risk
例如：
- UI 樣式
- 文案
- 不影響行為的小幅整理

驗證：
- 相關 Lint、Build 或 Type Check
- 必要的 UI 檢查

### Medium Risk
例如：
- REST API
- 一般商業邏輯
- SignalR 事件處理
- EF Core 資料操作

驗證：
- Build
- 相關 Unit Tests
- 必要的 Integration Tests
- 檢查 Error Handling 與 Edge Cases

### High Risk
例如：
- Anonymous Identity 與 Room Ownership
- Security Token 與 Authorization
- Concurrent Operations
- Database Transaction 與 Data Consistency
- Room Expiration 與資料刪除

驗證：
- Build
- 相關 Unit / Integration Tests
- Concurrency 與 Failure Scenarios
- Security / Data Consistency Review
- 必要時進行 Independent Code Review

### General Rules
- 優先執行與變更直接相關的測試。
- 依影響範圍決定是否執行完整測試。
- 不能以 Lint 或 Build 通過取代行為驗證。
- 不得宣稱未執行的測試已通過。
- 測試失敗時，必須保留並回報錯誤資訊。
- 不得為了通過測試而削弱測試的有效性。

## 8. Failure Handling & Stop Conditions

遇到錯誤時，應先分析原因再進行修正。

- 不得在缺乏新證據時重複相同操作。
- 不得以盲目重試取代問題分析。
- 優先修正 Root Cause，而非掩蓋症狀。
- 避免因局部失敗而進行大範圍重寫。

若連續兩次修正仍未解決同一問題：
1. 停止盲目修改。
2. 整理已嘗試的方案。
3. 提供 Error Logs 與相關證據。
4. 說明目前的阻礙與可能原因。
5. 提出下一步建議，等待使用者確認。

遇到破壞性操作、權限不足或需求衝突時，
應停止相關操作並要求確認。

## 9. Multi-Agent Collaboration

本專案允許不同 AI Agent 參與實作與審查。

預設合作模式：
- Implementation Agent：負責程式碼實作與測試。
- Review Agent：負責獨立檢查與提出問題。
- Human：負責需求、架構取捨及最終決策。

### Implementation Agent
- 遵循本文件與既有設計決策。
- 完成最小範圍的功能修改。
- 提供測試結果與變更摘要。
- 對 Review Findings 進行獨立驗證。
- 不盲目接受其他 Agent 的建議。

### Review Agent
- 根據需求、程式碼與測試獨立審查。
- 優先找出 Correctness、Security 與
  Data Consistency 問題。
- 提供具體程式碼位置與失敗情境。
- 區分已確認問題與潛在風險。
- 避免缺乏證據的推測及無關重構。

### Collaboration Rules
- 不以 Agent 間的共識取代測試證據。
- 不讓多個 Agent 同時修改同一工作目錄。
- Review Findings 必須經過驗證。
- 避免 Agent 之間無限制反覆審查。
- 不得自行合併分支或推送遠端變更，
  除非使用者明確要求。

## 10. Completion Report

完成任務後，請簡潔回報：

### Summary
- 本次完成的功能或修正。

### Changes
- 主要修改檔案及修改原因。

### Verification
- 實際執行的 Build、Lint、Tests。
- 測試結果。
- 未執行的測試及原因。

### Risks
- 已知限制、假設或未解決問題。

### Decisions
- 重要技術決策及 Trade-offs（若有）。

避免不必要的冗長報告。

## 11. Communication

- 所有說明使用正體中文（台灣用語）。
- 程式碼識別字使用英文。
- 引入新技術或 Design Pattern 時，
  應說明用途、替代方案與 Trade-offs。
- 本專案重視學習，應協助開發者理解設計，
  而非只提供完成的程式碼。
- 避免沒有實際價值的客套與重複說明。
