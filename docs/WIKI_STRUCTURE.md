# Multi-Room Audio Docker Controller - Wiki Structure

This document outlines the recommended GitHub Wiki structure for the Multi-Room Audio Docker Controller project. Each page follows a problem/solution format to help users quickly understand what they need and how to achieve it.

---

## Wiki Navigation Structure

```
Home (landing page)
|
+-- Getting Started
|   +-- Quick Start (5-minute setup)
|   +-- Choosing Your Player Backend
|
+-- Installation Guides
|   +-- Docker Deployment Guide
|   +-- HAOS Add-on Guide
|   +-- Platform-Specific Setup
|       +-- TrueNAS Scale
|       +-- Synology NAS
|       +-- Raspberry Pi
|       +-- Portainer
|       +-- Dockge
|
+-- Configuration
|   +-- Configuration Reference
|   +-- Audio Device Setup
|   +-- Environment Variables
|   +-- Player Type Comparison
|
+-- Usage
|   +-- Creating Players
|   +-- Music Assistant Integration
|   +-- LMS Integration
|   +-- Snapcast Integration
|   +-- REST API Reference
|
+-- Troubleshooting
|   +-- Common Issues
|   +-- Audio Device Problems
|   +-- Network Connectivity
|   +-- Player Won't Start
|
+-- For Developers
|   +-- Code Structure
|   +-- Architecture Overview
|   +-- Contributing Guide
|   +-- Building from Source
```

---

## Content Guidelines

### Problem/Solution Format

Every major page should include:

1. **The Problem**: What pain point does this address? (1-3 sentences)
2. **The Solution**: How does this project/feature solve it? (1-3 sentences)
3. **Quick Start**: Minimum steps to see it working

### Writing Style

- **Scannable**: Use headers, tables, and bullet points
- **Action-oriented**: Start with verbs ("Deploy", "Create", "Configure")
- **Specific**: Include exact commands and expected output
- **Visual**: Use diagrams where helpful
- **No jargon**: Define technical terms on first use

---

## Page Templates

### Home Page

**Purpose**: First thing users see. Answer "What is this?" and "Should I use it?"

```markdown
# Multi-Room Audio Docker Controller

**One server. Multiple audio outputs. Whole-home audio.**

## What Problem Does This Solve?

You want multi-room audio but:
- Commercial solutions (Sonos, HEOS) are expensive
- You already have speakers, amps, or DACs
- You want to integrate with Music Assistant or LMS
- You need something that runs on hardware you already own

## The Solution

Run a single Docker container on your NAS, Raspberry Pi, or any server.
Connect USB DACs or use built-in audio outputs. Each becomes an independent
audio zone controllable from Music Assistant, Logitech Media Server, or Snapcast.

## Quick Links

| I want to... | Go here |
|--------------|---------|
| Get running in 5 minutes | [Quick Start](Quick-Start) |
| Use with Home Assistant | [HAOS Add-on Guide](HAOS-Addon-Guide) |
| Deploy on my NAS | [Docker Deployment](Docker-Deployment) |
| Understand the options | [Player Type Comparison](Player-Comparison) |
| Fix something broken | [Troubleshooting](Troubleshooting) |

## Supported Backends

| Backend | Best For | Server Required |
|---------|----------|-----------------|
| **Sendspin** | Music Assistant users | Music Assistant |
| **Squeezelite** | LMS users, mixed environments | LMS or Music Assistant |
| **Snapcast** | Bit-perfect synchronized audio | Snapcast Server |
```

---

### Quick Start Page

**Purpose**: Get from zero to working audio in under 5 minutes.

**Key Sections:**
1. Prerequisites (one line)
2. Deploy command (copy-paste ready)
3. Create first player (numbered steps)
4. Verify in audio server
5. Next steps links

---

### Docker Deployment Page

**Purpose**: Complete production-ready deployment guide.

**Key Sections:**
1. **The Problem**: Need reliable, persistent multi-room setup
2. **The Solution**: Proper volume mounts, device access, environment config
3. Prerequisites
4. Deployment options (Compose vs Run)
5. Image variants table
6. Volume mounts reference
7. Device access by platform
8. Environment variables reference
9. Health check commands
10. Next steps

---

### HAOS Add-on Page

**Purpose**: Complete guide for Home Assistant OS users (SEPARATE from Docker).

**Key Sections:**
1. **The Problem**: Want multi-room audio native to HA
2. **The Solution**: Native add-on with sidebar integration
3. HAOS vs Docker comparison table (critical!)
4. Installation steps
5. Audio device setup (PulseAudio specifics)
6. Creating players
7. MA integration
8. Troubleshooting (HAOS-specific)
9. Known limitations

---

### Troubleshooting Page

**Purpose**: Help users solve common problems quickly.

**Key Sections:**
1. Quick diagnosis table (symptom -> likely cause -> solution link)
2. Problem sections with:
   - Problem statement
   - Docker solution
   - HAOS solution
   - Common causes
3. Log locations (Docker vs HAOS)
4. Getting help (issue template link)

---

## Page Naming Conventions

| Page | File Name | URL Slug |
|------|-----------|----------|
| Home | `Home.md` | `/wiki` |
| Quick Start | `Quick-Start.md` | `/wiki/Quick-Start` |
| Docker Deployment | `Docker-Deployment.md` | `/wiki/Docker-Deployment` |
| HAOS Add-on Guide | `HAOS-Addon-Guide.md` | `/wiki/HAOS-Addon-Guide` |
| Troubleshooting | `Troubleshooting.md` | `/wiki/Troubleshooting` |
| Configuration Reference | `Configuration-Reference.md` | `/wiki/Configuration-Reference` |
| Contributing | `Contributing.md` | `/wiki/Contributing` |
| Code Structure | `Code-Structure.md` | `/wiki/Code-Structure` |

---

## Sidebar Navigation (_Sidebar.md)

```markdown
**Getting Started**
* [Home](Home)
* [Quick Start](Quick-Start)

**Installation**
* [Docker Deployment](Docker-Deployment)
* [HAOS Add-on Guide](HAOS-Addon-Guide)
* [Platform Guides](Platform-Specific-Setup)

**Usage**
* [Creating Players](Creating-Players)
* [Configuration Reference](Configuration-Reference)

**Help**
* [Troubleshooting](Troubleshooting)
* [FAQ](FAQ)

**Development**
* [Contributing](Contributing)
* [Code Structure](Code-Structure)
* [Architecture](Architecture)
```

---

## Implementation Priority

### Phase 1: Essential
1. Home page
2. Quick Start
3. Docker Deployment
4. HAOS Add-on Guide
5. Troubleshooting

### Phase 2: Complete
1. Configuration Reference
2. Creating Players
3. Platform-Specific guides
4. Contributing

### Phase 3: Enhanced (Ongoing)
1. FAQ (based on issues)
2. Integration guides (MA, LMS, Snapcast deep dives)
3. Advanced use cases
4. Video tutorials (external links)

---

## Quality Checklist

Before publishing any wiki page:

- [ ] Problem/solution format used
- [ ] All commands tested and working
- [ ] Links to related pages included
- [ ] Correct for both Docker AND HAOS (or clearly marked as one)
- [ ] No broken internal links
- [ ] Tables render correctly
- [ ] Code blocks have syntax highlighting
- [ ] Reviewed for clarity and scannability
