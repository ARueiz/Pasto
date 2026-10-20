# Pasto — AI Agent Guidelines

## 專案概述

Pasto 是多人即時共享剪貼簿 Web Application。

MVP 支援匿名建立房間、分享連結、多筆文字內容及 SignalR 即時同步。

專案同時用於學習 System Design、Scalability、Concurrency 與 AI-assisted Development。

## Tech Stack

- Frontend：React + TypeScript + Vite
- Backend：ASP.NET Core (.NET 10)、SignalR
- Database：PostgreSQL + EF Core
- Infrastructure：Docker Compose
- Architecture：Modular Monolith

## Architecture Guidelines

- 採用 API、Application、Domain、Infrastructure 分層，保持依賴方向清楚。
- PostgreSQL 為 Source of Truth，REST API 負責資料操作，SignalR 負責即時通知。
- 資料交易成功後才發送通知；Client 重新連線時應能重新同步。
- 以大量使用者為設計前提，考慮 Concurrency、Index、Pagination、Rate Limiting 與 Security。
- 避免過度設計；未有明確需求前，不主動引入 Microservices、Redis 或 Message Queue。
- 優先保持程式碼簡單、可讀、可測試。

## Coding Guidelines

- Backend 使用 async/await、Dependency Injection 與 Structured Logging。
- Frontend 使用 React Functional Components、Hooks 與 TypeScript。
- 注意輸入驗證、錯誤處理、資源限制與敏感資訊保護。
- 重要商業邏輯應有測試；修改後執行相關 Build、Lint、Tests。
- 不得宣稱未實際執行的測試已通過。

## Agent Workflow

1. 修改前先閱讀相關程式碼與文件。
2. 重大變更先提出方案、理由及 Trade-offs，等待確認。
3. 採用小範圍、可驗證的修改，避免無關的 Refactoring。
4. 不擅自增加依賴、擴大需求或覆蓋既有修改。
5. 完成後說明變更內容、測試結果及已知風險。

## Communication

- 所有說明使用正體中文（台灣用語），程式碼識別字使用英文。
- 引入新技術或 Design Pattern 時，說明用途、替代方案與 Trade-offs。
- 本專案重視學習，應協助開發者理解設計決策，而非只產生程式碼。
- 詳細需求與架構決策分別參考 `docs/requirements.md`、`docs/architecture.md` 及 `docs/decisions/`（若存在）。