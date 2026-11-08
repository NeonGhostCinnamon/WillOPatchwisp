# Will-o-Patchwisp

Patch notes for your Dalamud plugins.

- **Recent**: whenever plugins update, see what changed: old → new version plus the changelog.
- **Available**: checks your plugin repositories for updates you haven't installed yet, and installs them with one click (or all at once).
- **All plugins**: the current changelog of every plugin you have, newest release first.
- No changelog? It looks for the matching **GitHub release** (or recent commits) instead.
- Ignore list for plugins you don't want update alerts for.
- Light and dark theme, a compact minimized bar with update badges.

## Install

1. In game, type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this URL (it has all of my plugins), click **+**, tick **Enabled** and **Save and Close**:

   ```
   https://raw.githubusercontent.com/NeonGhostCinnamon/WillOPlugins/HEAD/repo.json
   ```

3. Type `/xlplugins`, search for **Will-o-Patchwisp** and install it.

Open it with `/patchwisp`.

## Notes

- The one-click **Update** buttons use Dalamud's own internal updater. That isn't official Dalamud API, so if a future Dalamud version changes it, the buttons fall back to opening Dalamud's plugin installer on the updates page.
- GitHub lookups only happen when you open a card without a changelog, and results are cached (GitHub allows about 60 lookups per hour without an account).
- Font: Nunito Black, © The Nunito Project Authors, SIL Open Font License (see `Nunito-OFL.txt`).

## Releasing a new version (for the author)

Commit your changes, then push a version tag. GitHub builds the plugin, creates a release and updates `repo.json` automatically:

```
git push
git tag v2.5.0
git push --tags
```

The commit messages since the previous tag become the changelog shown in Dalamud (and in Will-o-Patchwisp itself).

## AI usage

AI was used to help create this plugin.
