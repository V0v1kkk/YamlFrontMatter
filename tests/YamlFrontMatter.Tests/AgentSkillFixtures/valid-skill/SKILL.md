---
name: valid-skill
description: A fully valid skill adhering to agent-skills specification.
license: MIT
compatibility: Requires Python 3.10+ and Linux
allowed-tools: Read Bash(python:*)
metadata:
  author: Vladimir Rogozhin
  dev.v-san.skills: |-
    origin: original (personal skill)
    version: '1.0'
    changeDate: '2026-08-11'
    authors: [Vladimir Rogozhin]
    privateSkill: false
    skillGroups: [fsharp, fsharp-core]
    dependsOnSkills: [another-skill]
    requires:
      dependencies: [Python 3.10+, jq]
      environmentVariables: [REQUIRED_TOKEN]
      optionalEnvironmentVariables: [OPTIONAL_TOKEN]
      platforms: [linux, macos]
    sources:
      - https://example.com/primary-source
    book:
      title: Extensible Pattern Matching
      authors: [Don Syme, Gregory Neverov]
      year: 2007
      language: eng
      isbn: '9781595938152'
    hermes:
      category: content
      tags: [x, posts, threads]
      relatedSkills: []
    upstream:
      repository: https://github.com/example/upstream
      path: skills/valid-skill
      commit: 0123456789abcdef0123456789abcdef01234567
      version: '1.2.3'
      changeDate: '2026-08-01'
---
# Valid Skill
This is the body.
