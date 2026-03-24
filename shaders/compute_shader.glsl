#[compute]
#version 450

// Workgroup size
layout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;

// 3D input texture (density field)
layout(r8_snorm, set = 0, binding = 0) uniform image3D density_tex;
layout(r8_snorm, set = 0, binding = 1) uniform readonly image3D dig_tex;
layout(std430, set = 0, binding = 2) buffer offset_buffer {
  ivec3 offset;
}offB;

void main() {
  // ivec3 offset = ivec3(0, 0, 0);
  ivec3 uv = ivec3(gl_GlobalInvocationID.xyz);
  // imageSize(img);

  if (!all(greaterThanEqual(uv, offB.offset))) {
    return;
  }
  vec4 color = imageLoad(density_tex, uv);

  ivec3 dig_uv = uv - offB.offset;
  ivec3 dig_size = imageSize(dig_tex);

  if (any(lessThan(dig_uv,ivec3(0)))) {
    return;
  }

  if (any(greaterThanEqual(dig_uv, dig_size))) {
    return;
  }

  vec4 dig_color = imageLoad(dig_tex, dig_uv);
  color *= dig_color;

  imageStore(density_tex, uv, color);
}