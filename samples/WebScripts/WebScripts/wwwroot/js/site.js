function add(a, b) {
  const sum = a + b;
  return sum; // [sum]
}

setTimeout(() => {
  document.getElementById('title').textContent = 'Sum: ' + add(2, 3);
}, 500);
