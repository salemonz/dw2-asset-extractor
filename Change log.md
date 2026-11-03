# Change Log

## add-scripts

- Added `run` and `build` scripts to make new installs easy
  - available in both `.bat` and `.ps1` format to make it easy for non-programmers to bootstrap this tool
  - build will auto-install needed 3rd party tools
  - will auto detect where DW2 is installed for both Steam and GOG versions
  - allows user to override specific install to use
  - prepopulates the resulting tool with this information
  - run will automatically capture the full output log to `LastRun.log` for reference
- Added high DPI aware init
- Tweaked dialog layouts for high DPI displays
- Improved external tool calls to better avoid hang conditions
- Added user-controllable timeouts for external tools so that they cannot hang forever
- Improved failure detection and reporting so that a list of failures is presented in the summary at the end
- Defaults to using this repo's Output folder for extracted data (user overridable each time)
- Updated README.md in the root to call out using `build` and `run` as the easier way to use this tool

## parallel

- Automatically runs the extraction of each bundle in parallel
- You can set the limit to parallelism via cli or settings file (default is 1/2 your logical cores or whole cpu for small cpus)
- Automatically generates dw2extractor.log file with details so you can refer back to it
- Added -v || --verbose mode to generate extra data about parallel performance
- Added additional summary information for the run as a whole

## skip-existing

- Added -overwrite and -skip-existing CLI flags
- Without specifying one or the other, defaults to asking the user interactively which they prefer IF the target folder is not empty

## further-improvements

- now has conversion level parallelism for radically improved performance
- added recreate mode that first cleans the target folder than regenerates the requested files
- fixed ffmpeg color handling for stride bundles

## cli-help-and-types

- Added `-types <list>` to pick asset types from the command line (`dds`, `png`, `textures`, `wav`/`sounds`, `fbx`/`meshes`, `misc`, `all`), e.g. `-types textures` for DDS + PNG only. Also skips the asset-type checklist in interactive mode
- Unknown options and wrong argument counts now print the error and usage line and exit with code 2, instead of being taken as paths
- `--help` (also `-h`, `-?`, `/?`, `-help`) is checked before any other argument, returns normally so the log is flushed, and documents `-types` and when a file mode is required
