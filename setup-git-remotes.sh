#!/bin/bash
# Git remote configuration for fork workflow
# Run this after cloning the repository to prevent accidental pushes to upstream

echo "Configuring git remotes for fork workflow..."

# Disable push to upstream (fetch only)
git remote set-url --push upstream no_push

# Set safe push default
git config push.default simple

# Verify configuration
echo ""
echo "✅ Configuration complete!"
echo ""
echo "Remote configuration:"
git remote -v
echo ""
echo "Push default: $(git config push.default)"
echo ""
echo "You can now safely work with feature branches without accidentally pushing to upstream."
