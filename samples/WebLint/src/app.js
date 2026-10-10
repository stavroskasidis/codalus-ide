// Lint and format fixture: an unused variable, a loose comparison, and code Prettier reformats.
export function total(items) {
  const unused = 42; // [unused]
  let sum = 0
  for (const item of items) { if (item.count == 0) { continue } sum += item.count }
  return sum;
}

console.log(total([{ count: 1 }, { count: 2 }]))
