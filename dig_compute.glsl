#[compute]
#version 450

// Workgroup size
layout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;

layout(r8_snorm, set = 0, binding = 0) uniform writeonly image3D dig_tex;

void main() {
  ivec3 uv = ivec3(gl_GlobalInvocationID.xyz);
  ivec3 size = imageSize(dig_tex);
  vec3 center = vec3(size)/2.0;

  vec4 color = vec4(1.0, 1.0, 1.0, 1.0);

  float dist = distance(center, uv)/center.x;
  color *= dist;

  imageStore(dig_tex, uv, color);
}