// // step 1 define the space
// worldMin = (0,0,0)
// worldMax = (4,4,4)
// cellSize = 1

// This creates a grid:

// 4 × 4 × 4 cells
// 5 × 5 × 5 grid points (because corners)

// Marching Cubes evaluates density at each grid point.

// // step 2 define my shapes mathmatically 
// Example 1: Sphere

// Center = (2,2,2)
// Radius = 1.5

// density = radius - distance(point, center)
// If density > 0 → inside
// If density < 0 → outside

// Example 2: Box
// Center = (1.5, 1.5, 1.5)
// Half size = (1,1,1)

// Simple inside test (not smooth SDF version):
// if point.x between [0.5 , 2.5]
// and point.y between [0.5 , 2.5]
// and point.z between [0.5 , 2.5]
//     density = 1
// else
//     density = -1

//     Step 3 — Merge Shapes

// To merge sphere and box:

// density(point) = max(densitySphere, densityBox)

// Why max?

// Because if either shape says “inside”, the union is inside.