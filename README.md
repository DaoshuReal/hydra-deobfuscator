# hydra-deobfuscator - Hydra-protected .NET deobfuscator

i wanted to actually read a Hydra-obfuscated .NET assembly instead of scrolling through state machines, encrypted strings and proxy calls. so i built hydra-deobfuscator, a small dnlib-based deobfuscator that recovers the Hail key, inlines int/string proxies, decrypts strings, linearizes switch dispatchers and strips the leftover junk.

to be clear: this is a poc that grew into a tool, not a universal Hydra unpacker. it was built and tested against one Hydra config on one sample (`mod.dll`: 240 types / 9921 methods in, 133 types / 549 methods out). everything below about what it handles is stuff i verified on that sample, not a promise about other configs.

## what is this?

hydra-deobfuscator is a .NET 10 console tool (dnlib 4.3.0, no other dependencies) that rewrites a Hydra-protected assembly in place:

- **Hail recovery** — `HailHydra` resource (`XOR 0xAA` over 32 bytes), `stsfld`-heavy init fields, int-returning getters
- **int proxies** — seeding from `_<Owner>_<value>` names with emulation fallback, audit against the suffix, then inline to `ldc.i4` (9073 calls on the test sample)
- **string proxies** — `Decrypt`-based wrappers inlined to `ldstr` (72 on the test sample, `DynamicMethod` wrappers skipped and handled later)
- **string decryption** — `Module.Decrypt` (`ldstr` + 5 key `ldc`s, key is slot 2), combined Caesar + Base64 + XOR (`_25982` + `_90841`) with key brute-force over all 5 slots, and V2 dynamic `Emit` payloads (122 payloads, 121 patched)
- **control flow** — peephole optimizer (assert / ld-pop / sizeof / confuse / const-fold / branch-fold / nop / dead-store), state-dispatcher removal via worklist propagation, header-only collapse, guarded `N=3` dispatcher cleanup, dead-code elimination
- **final cleanup** — proxy/junk-type removal (9145 methods, 106 types on the sample), string-infrastructure linearization and deletion, renames (`AntiDebug`, `DecryptCaesar`, `DecryptBase64`, `GetXorKey`, `AntiTamperCheck`), anti-tamper call stripping

## how it works

1. **analysis** — locate the central obfuscator type (scored by `stsfld`-heavy init plus Hail getters plus string/byte decryptor shapes), extract the Hail key, collect Hail fields from the `stsfld`-heavy init, map Hail getters into int proxies
2. **int proxies** — seed parseable `_<Owner>_<value>` names first (they win over emulation), emulate the remainder with a small int-only IL interpreter, audit emulated values against the name suffix, inline all calls
3. **strings round 1** — collect + inline string proxies, patch direct `Module.Decrypt` calls, try combined calls, run V2 against dynamic payloads, peephole
4. **strings round 2** — repeat decrypts now that `ldstr`/`call` pairs sit adjacent after peephole, remove state dispatchers, clean guarded dispatchers, peephole, dead-code
5. **strings rounds 3-4** — same decrypt/dispatcher/peephole/dead-code loop two more times, because each dispatcher removal creates new adjacencies; finish with header-only collapse
6. **final cleanup** — report remaining switches/strings, delete proxies and auto-detected junk types (pure proxy containers plus init-chain types traced from the module `.cctor`, plus `dummy_ptr`, unused `HydraNoObfuscate`, module `.cctor` junk calls), linearize and delete dead string infrastructure, rename, strip anti-tamper calls, write with `NoThrow` logger

string keys are never trusted blindly: combined decrypts brute-force all 5 key slots and only accept a result that base64-decodes, XORs to valid UTF-8 and looks like plausible plaintext (mostly ASCII, or a short all-printable fallback for 1-char JSON punctuation). no plausible decode means no patch.

dispatchers are `ldc N; rem; switch[N]` headers with `br Head` back-edges. states propagate over a worklist with concrete ints and `Unknown` for real data. full rewrite needs a known entry state plus one shared EH region; otherwise each safe back-edge converts individually and the header stays live. anything imprecise skips the method instead of guessing.

## project structure

```
hydra-deobfuscator/
├── HydraDeobfuscator.sln
├── README.md
├── src/HydraDeobfuscator/
│   ├── Program.cs                   # arg parsing, load, run pipeline, write
│   ├── Core/
│   │   ├── DeobfuscationContext.cs  # module, central type, proxy/payload maps, Hail key
│   │   └── DeobfuscationPipeline.cs # pass order (rounds 1-4 + cleanup)
│   ├── Logging/
│   │   └── HydraLogger.cs           # phased ANSI logger, VT enable on Windows, plain on redirect
│   ├── Analysis/
│   │   ├── CentralTypeFinder.cs     # central-type scoring (init stores plus getters plus decryptors)
│   │   ├── HailKeyExtractor.cs      # HailHydra resource + fallback key
│   │   ├── HailFieldExtractor.cs    # stsfld-heavy init scan
│   │   └── HailGetterMapper.cs      # ldsfld getters into int proxies
│   ├── Proxies/
│   │   ├── IntProxySeeder.cs        # _Owner_value name seeding
│   │   ├── IntProxyEmulator.cs      # fallback emulation + suffix audit
│   │   ├── IntProxyInliner.cs       # call -> ldc.i4
│   │   ├── StringProxyCollector.cs  # Decrypt-wrapper collection
│   │   └── StringProxyInliner.cs    # call -> ldstr
│   ├── Strings/
│   │   ├── CaesarCipher.cs          # Module / combined / Base64+XOR primitives
│   │   ├── StringProxyReader.cs     # ldstr + 5x ldc extraction
│   │   ├── ModuleDecryptor.cs       # direct Module.Decrypt patching
│   │   ├── CombinedDecryptor.cs     # Caesar + Base64 brute-force patching
│   │   ├── DecryptorFinder.cs       # Caesar (6 args) / Base64 (1 arg) / XorKey lookup
│   │   ├── DynamicPayloadExtractor.cs # Emit ldstr payload recovery
│   │   ├── StringDecryptorV2.cs     # GetString + 5 keys + Caesar + Base64 patching
│   │   └── StringInfrastructureCleaner.cs # linearize decryptors, delete dead payloads
│   ├── ControlFlow/
│   │   ├── PeepholeOptimizer.cs     # 12-iter assert/ldpop/sizeof/confuse/const/branch/nop/deadstore
│   │   ├── DeadCodeEliminator.cs    # reachability + unreachable removal
│   │   ├── RegionHelpers.cs         # EH region keys, reachability sets
│   │   ├── StatePropagator.cs       # worklist state + stack propagation
│   │   ├── StateDispatcherRemover.cs # header detect, verify, br-to-target rewrite
│   │   ├── HeaderOnlyCollapser.cs   # single-fire header -> br First
│   │   └── GuardedDispatcherCleaner.cs # N=3 Rem_Un guarded-switch cleanup
│   ├── Cleanup/
│   │   ├── JunkRemover.cs           # proxies, chain and junk types, dummy_ptr, cctor calls
│   │   ├── Renamer.cs               # AntiDebug, Decrypt*, AntiTamperCheck, MainConfig
│   │   ├── AntiTamperStripper.cs    # nop AntiTamperCheck / AntiDebugInit_* calls
│   │   └── ObfuscationReporter.cs   # remaining switch/string inventory
│   └── Il/
│       ├── IlHelpers.cs             # ldc/local/branch/plaintext/region helpers
│       └── IntEmulator.cs           # int-only method emulator (Random/Math/Convert aware)
```

## prerequisites

- **Windows x64** (tested here; the tool itself is plain .NET and should run anywhere .NET 10 runs)
- **.NET 10 SDK**
- NuGet access for `dnlib 4.3.0` (only dependency)

## building

```
dotnet build HydraDeobfuscator.sln -c Release
```

the binary ends up in `src/HydraDeobfuscator/bin/Release/net10.0/HydraDeobfuscator.exe`.

## usage

```
HydraDeobfuscator.exe <input.dll> <output.dll> [--verbose]
```

run it with:

```
HydraDeobfuscator.exe mod.dll mod.clean.dll
```

add `--verbose` (`-v`) for per-payload detail. `--help` (`-h`) prints usage. both paths are required, there are no defaults. output is written with dnlib `NoThrow` logger settings.

## example output

```
  ━━ [02] INTEGER PROXIES ────────────────────────────────
  ➜ Seeding proxies from names
  ✔ Seeded int proxies from names: 8964
  ➜ Emulating int proxies
  ✔ Emulated ints attempted=0 succeeded=0 total=9073
    ╰─ checked=8964 · match=8964 · mismatch=0
  ➜ Inlining int proxies
  ✔ Inlined 9073 int calls

  ━━ [04] STRINGS ROUND 2 ────────────────────────────────
  ➜ Removing state dispatchers
  ✔ State dispatchers done=237 skip=9583 converted=1841 demoted=0
    │ [skip] eh-region: 59
    │ [skip] no-header: 33

  ╔═ DONE ═══════════════════════════════════════╗
   Types   133   ·   Methods   549   ·   Elapsed   4.3s
   Output  mod.clean.dll
  ╚═════════════════════════════════════════════╝
```

on the test sample: `Module.Decrypt` 523 patched, combined 72, V2 121 (30 short), guarded dispatchers 4, junk 9145 methods / 106 types, renames 47, anti-tamper 40 methods / 77 calls nopped.

## notes

- logging uses ANSI colors with `ENABLE_VIRTUAL_TERMINAL_PROCESSING` enabled on Windows, and degrades to plain text when redirected or when `NO_COLOR` is set
- the pipeline runs decrypt → peephole → dispatcher passes four times on purpose. each dispatcher removal reorders blocks and creates new `ldstr`/`call` adjacencies, so one pass is never enough
- name seeding wins over emulation for int proxies. the audit showed ~all parseable names matching emulation on the sample, so trusting the suffix first is both faster and safer there
- `DynamicMethod` string wrappers are never inlined directly. their `Emit` payload is extracted and decrypted at the call site instead
- string infrastructure (decryptors, payload getters) is only linearized and deleted after the last string pass, and payloads with remaining callers are kept so no dangling calls are left behind

## limitations

- **one-sample tool.** Hydra configs drift (names, key counts, dispatcher shapes). anything that doesn't match the expected shapes is skipped, not forced
- **switches remain.** post-junk the sample still has 10 methods with `switch`: 4 without EH (2 are real `SimpleJson` switches, 1 is `MessageBoxAPI::Show`, 1 is a leftover central payload getter) and 6 with EH (async state machines and a deserializer). those are either real code or handler-contained dispatchers the region gate refused to touch
- **one string call remains** (`ServerBrowser1::Postfix`). no plausible key decoded it, so it was left alone rather than patched with garbage
- **edge-only mode leaves headers live.** mixed-region and handler-contained dispatchers convert safe back-edges and keep the original switch for the rest. correct, but noisy in diffs
- **name seeding trusts the suffix.** if a future config names a real helper `Foo_123` that isn't a proxy, it gets folded wrong. the leading-underscore + `int32()` + static gate is the only guard
- **plaintext filter is ASCII-biased.** 80% printable ASCII plus 2 alphanumerics, with a short-printable fallback. legit non-English strings would get skipped
- **empty strings are only patched on exact key spans.** an empty payload with ambiguous surrounding `ldc`s is left for the runtime to evaluate instead of risking dispatcher state

## what i learned

- int-proxy names are ground truth when the obfuscator is lazy: `_<Owner>_<value>` beats emulation, and the audit loop that proves it is worth more than the emulator itself
- string decryption is a key-index problem, not a crypto problem. Hydra's Caesar key is always slot 2 of 5, but after control-flow obfuscation the 5 you see aren't always the 5 you want, so brute-forcing all slots plus a plausibility vote is the only stable approach
- `ldc N; rem; switch[N]` plus counted `br Head` back-edges is a reliable dispatcher fingerprint, but frequency alone elects real join points too. the pure-header gate (stack juggling only, no calls/fields/real branches) is what keeps handler finally-dispatchers alive
- full header replacement needs both a known entry state and one shared EH region. without either, per-edge conversion with the header left live is the sound fallback
- multi-round string/dispatcher/peephole loops aren't inefficiency, they are the algorithm. each pass creates the adjacencies the next pass needs
- `PAGE_GUARD`-style thinking applies to IL too: never patch what you can't verify. every converted edge re-emulates its update span from an empty stack and must leave exactly one value, otherwise the method is skipped whole
