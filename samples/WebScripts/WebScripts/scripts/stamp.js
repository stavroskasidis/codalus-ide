// Bound to Before Build: proves the binding ran.
require('fs').writeFileSync('stamp.txt', 'built at ' + new Date().toISOString() + '\n');
