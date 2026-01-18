# Fork Workflow Setup

This repository uses a fork-based workflow to prevent accidental pushes to the upstream repository.

## Initial Setup (One-time per clone)

After cloning your fork, run the setup script:

```bash
./setup-git-remotes.sh
```

This script will:
- Disable direct pushes to `upstream` (you can still fetch/pull)
- Configure safe push defaults
- Ensure all feature branches push to your fork by default

## Workflow for New Features

### 1. Create a feature branch from upstream

```bash
# Fetch latest from upstream
git fetch upstream

# Create feature branch from upstream/dev
git checkout -b feature/my-feature upstream/dev

# Set tracking to YOUR fork (important!)
git push -u origin feature/my-feature
```

### 2. Work on your feature

```bash
# Make changes and commit
git add .
git commit -m "Your commit message"

# Push to YOUR fork
git push
```

### 3. Create a Pull Request

When ready, create a PR from your fork to upstream:

```
From: scyto/Multi-SendSpin-Player-Container:feature/my-feature
To:   chrisuthe/Multi-SendSpin-Player-Container:dev
```

## Troubleshooting

### "fatal: 'no_push' does not appear to be a git repository"

This is expected! It means the protection is working - you tried to push to upstream directly. Push to your fork instead:

```bash
git push origin your-branch-name
```

### Check your remote configuration

```bash
git remote -v
```

Should show:
```
origin    https://github.com/YOUR-USERNAME/Multi-SendSpin-Player-Container.git (fetch)
origin    https://github.com/YOUR-USERNAME/Multi-SendSpin-Player-Container.git (push)
upstream  https://github.com/chrisuthe/Multi-SendSpin-Player-Container.git (fetch)
upstream  no_push (push)
```

### Check branch tracking

```bash
git branch -vv
```

Feature branches should track `origin/feature-name`, not `upstream/dev`.

## Manual Setup

If you prefer not to use the script, run these commands:

```bash
# Disable push to upstream
git remote set-url --push upstream no_push

# Set safe push behavior
git config push.default simple
```
