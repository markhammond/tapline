# Third-party notices

`opaque.wasm` is built from this sample's code and the crates below (from crates.io, as `wasm/Cargo.lock` pins them),
with Rust 1.94.0's standard library (MIT OR Apache-2.0). Where a crate offers Apache-2.0, it is used under
Apache-2.0, whose text is in `LICENSE`; none of those crates has a NOTICE file. The crates licensed otherwise follow
in full, as are the copyright statements some crates keep beside their licenses. Procedural macros used to build it
(`derive-where`, `displaydoc`, `zeroize_derive` and their dependencies) run at compile time only: none of their code
is in it.

| Crate | Version | License | Used under |
|---|---|---|---|
| opaque-ke | 4.0.1 | Apache-2.0 OR MIT | Apache-2.0 |
| voprf | 0.5.0 | MIT | MIT |
| curve25519-dalek | 4.1.3 | BSD-3-Clause | BSD-3-Clause |
| argon2 | 0.5.3 | MIT OR Apache-2.0 | Apache-2.0 |
| blake2 | 0.10.6 | MIT OR Apache-2.0 | Apache-2.0 |
| password-hash | 0.5.0 | MIT OR Apache-2.0 | Apache-2.0 |
| base64ct | 1.8.3 | Apache-2.0 OR MIT | Apache-2.0 |
| base16ct | 0.2.0 | Apache-2.0 OR MIT | Apache-2.0 |
| elliptic-curve | 0.13.8 | Apache-2.0 OR MIT | Apache-2.0 |
| crypto-bigint | 0.5.5 | Apache-2.0 OR MIT | Apache-2.0 |
| sec1 | 0.7.3 | Apache-2.0 OR MIT | Apache-2.0 |
| der | 0.7.10 | Apache-2.0 OR MIT | Apache-2.0 |
| const-oid | 0.9.6 | Apache-2.0 OR MIT | Apache-2.0 |
| ff | 0.13.1 | MIT/Apache-2.0 | Apache-2.0 |
| group | 0.13.0 | MIT/Apache-2.0 | Apache-2.0 |
| digest | 0.10.7 | MIT OR Apache-2.0 | Apache-2.0 |
| block-buffer | 0.10.4 | MIT OR Apache-2.0 | Apache-2.0 |
| crypto-common | 0.1.7 | MIT OR Apache-2.0 | Apache-2.0 |
| sha2 | 0.10.9 | MIT OR Apache-2.0 | Apache-2.0 |
| hkdf | 0.12.4 | MIT OR Apache-2.0 | Apache-2.0 |
| hmac | 0.12.1 | MIT OR Apache-2.0 | Apache-2.0 |
| generic-array | 0.14.7 | MIT | MIT |
| typenum | 1.20.1 | MIT OR Apache-2.0 | Apache-2.0 |
| subtle | 2.6.1 | BSD-3-Clause | BSD-3-Clause |
| zeroize | 1.9.1 | Apache-2.0 OR MIT | Apache-2.0 |
| rand | 0.8.8 | MIT OR Apache-2.0 | Apache-2.0 |
| rand_core | 0.6.4 | MIT OR Apache-2.0 | Apache-2.0 |
| cfg-if | 1.0.5 | MIT OR Apache-2.0 | Apache-2.0 |

## Licenses and copyright statements

### voprf 0.5.0 — LICENSE-MIT

```
Permission is hereby granted, free of charge, to any
person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the
Software without restriction, including without
limitation the rights to use, copy, modify, merge,
publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software
is furnished to do so, subject to the following
conditions:

The above copyright notice and this permission notice
shall be included in all copies or substantial portions
of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF
ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED
TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A
PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT
SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY
CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR
IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.
```

### curve25519-dalek 4.1.3 — LICENSE

```
Copyright (c) 2016-2021 isis agora lovecruft. All rights reserved.
Copyright (c) 2016-2021 Henry de Valence. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

1. Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
contributors may be used to endorse or promote products derived from
this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS
IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED
TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A
PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED
TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE. 

========================================================================

Portions of curve25519-dalek were originally derived from Adam Langley's
Go ed25519 implementation, found at <https://github.com/agl/ed25519/>,
under the following licence:

========================================================================

Copyright (c) 2012 The Go Authors. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

   * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
   * Neither the name of Google Inc. nor the names of its
contributors may be used to endorse or promote products derived from
this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS
IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED
TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A
PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER
OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### group 0.13.0 — COPYRIGHT

```
Copyrights in the "group" library are retained by their contributors. No
copyright assignment is required to contribute to the "group" library.

The "group" library is licensed under either of

 * Apache License, Version 2.0, (see ./LICENSE-APACHE or http://www.apache.org/licenses/LICENSE-2.0)
 * MIT license (see ./LICENSE-MIT or http://opensource.org/licenses/MIT)

at your option.

Unless you explicitly state otherwise, any contribution intentionally
submitted for inclusion in the work by you, as defined in the Apache-2.0
license, shall be dual licensed as above, without any additional terms or
conditions.
```

### generic-array 0.14.7 — LICENSE

```
The MIT License (MIT)

Copyright (c) 2015 Bartłomiej Kamiński

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### subtle 2.6.1 — LICENSE

```
Copyright (c) 2016-2017 Isis Agora Lovecruft, Henry de Valence. All rights reserved.
Copyright (c) 2016-2024 Isis Agora Lovecruft. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

1. Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
contributors may be used to endorse or promote products derived from
this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS
IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED
TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A
PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED
TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### rand 0.8.8 — COPYRIGHT

```
Copyrights in the Rand project are retained by their contributors. No
copyright assignment is required to contribute to the Rand project.

For full authorship information, see the version control history.

Except as otherwise noted (below and/or in individual files), Rand is
licensed under the Apache License, Version 2.0 <LICENSE-APACHE> or
<http://www.apache.org/licenses/LICENSE-2.0> or the MIT license
<LICENSE-MIT> or <http://opensource.org/licenses/MIT>, at your option.

The Rand project includes code from the Rust project
published under these same licenses.
```

### rand_core 0.6.4 — COPYRIGHT

```
Copyrights in the Rand project are retained by their contributors. No
copyright assignment is required to contribute to the Rand project.

For full authorship information, see the version control history.

Except as otherwise noted (below and/or in individual files), Rand is
licensed under the Apache License, Version 2.0 <LICENSE-APACHE> or
<http://www.apache.org/licenses/LICENSE-2.0> or the MIT license
<LICENSE-MIT> or <http://opensource.org/licenses/MIT>, at your option.

The Rand project includes code from the Rust project
published under these same licenses.
```
