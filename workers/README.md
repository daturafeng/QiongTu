# Worker boundaries

This directory reserves isolated process boundaries for photogrammetry, Gaussian training, tileset conversion, and export workers defined by OpenSpec task 4.1.

Task 2.1 does not include an algorithm implementation. Future workers must consume an immutable input manifest, emit versioned structured events, write only inside an assigned work directory, and publish outputs only through `QiongTu.Control` after validation.
