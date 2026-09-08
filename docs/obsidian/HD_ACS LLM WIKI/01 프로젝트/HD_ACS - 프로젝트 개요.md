---
project: HD_ACS
type: project
status: documented
updated: 2026-09-07
tags:
  - hd-acs
  - llm-wiki
---

# 프로젝트 개요

HD_ACS는 LNG 화물창의 용접검사 시나리오·영역·작업을 관리하고, HD_AMR에 이동 및 검사 명령을 전달하는 관제 시스템입니다.

## 책임

- HD_ACS: 검사 계획, 영역·작업 관리, 정차점·정차각 계산, 배차, 상태 대조, 기록 및 운영 UI.
- HD_AMR: AMR 주행, 협동로봇 자세·검사 시퀀스, 검사장비 제어.
- 이미지 전달 경로와 판정은 별도 검사 시스템의 책임이며 ACS가 하드웨어를 직접 제어하지 않습니다.

통신 상대와 계약은 [[HD_ACS - AMR 인터페이스]]가 정리합니다. 오래된 프로젝트 개요의 ROS2/REST 후보 설명을 현재 계약으로 읽지 않습니다.

## 개발 범위의 읽는 법

기존 개발 가이드는 PHASE 1(온보드 검사 시퀀스)을 완료, PHASE 2(ACS 관제)를 진행 중으로 기록합니다. 이 위키 작성 중 실제 로봇이나 전체 시스템을 재검증한 것은 아닙니다.

현재 확인한 코드·산출물의 범위는 [[HD_ACS - 현재 상태]], 향후 작업은 [[HD_ACS - 다음 작업]]을 봅니다.

근거: [[HD_ACS - 근거 목록]]의 `PROJECT_OVERVIEW.md`, `VDA5050_INTERFACE_SPEC.md`, `DEVELOPMENT_GUIDE.md`.
